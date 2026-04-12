using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Quartz;
using Robust.Cdn.Config;
using Robust.Cdn.Helpers;
using Robust.Cdn.Jobs;
using Robust.Cdn.Services;
using SpaceWizards.Sodium;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Robust.Cdn.Controllers;
[ApiController]
[Route("/robust/publish")]
public sealed partial class RobustPublishController(
    RobustAuthHelper authHelper,
    IHttpClientFactory httpFactory,
    Database cdnDatabase,
    ManifestDatabase manifestDatabase,
    ISchedulerFactory schedulerFactory,
    BaseUrlManager baseUrlManager,
    BuildDirectoryManager buildDirectoryManager,
    PublishManager publishManager,
    IOptions<RobustOptions> robustOptions,
    ILogger<RobustPublishController> logger)
    : ControllerBase
{
    private const string ForkName = UpdateRobustManifestJob.ForkName;

    private static readonly Regex ValidVersionRegex = ValidVersionRegexBuilder();
    private static readonly Regex ValidFileRegex = ValidFileRegexBuilder();

    [HttpPost("start")]
    public async Task<IActionResult> MultiPublishStart(
        [FromBody] PublishMultiRequest request,
        CancellationToken cancel)
    {
        if (!authHelper.IsAuthValid(out var failureResult))
            return failureResult;

        baseUrlManager.ValidateBaseUrl();

        if (!ValidVersionRegex.IsMatch(request.Version))
            return BadRequest("Invalid version name");

        var opts = robustOptions.Value;

        await using var tx = await manifestDatabase.Context.Database.BeginTransactionAsync(cancel);

        logger.LogInformation("Starting robust multi publish version {Version}", request.Version);

        var forkId = manifestDatabase.Context.Forks
            .Where(f => f.Name == ForkName)
            .Select(f => f.Id)
            .First();

        var hasExistingPublish = manifestDatabase.Context.PublishInProgresses
            .Any(p => p.Version == request.Version && p.ForkId == forkId);
        if (hasExistingPublish)
        {
            logger.LogWarning("Already had an in-progress robust publish for this version, aborting it and restarting.");
            publishManager.AbortMultiPublish(ForkName, request.Version);
        }

        var newPublish = new PublishInProgress
        {
            Version = request.Version,
            ForkId = forkId,
            StartTime = DateTime.UtcNow,
            EngineVersion = request.EngineVersion
        };
        manifestDatabase.Context.PublishInProgresses.Add(newPublish);
        await manifestDatabase.Context.SaveChangesAsync(cancel);

        var inProgressDir = buildDirectoryManager.GetRobustInProgressPublishPath(request.Version);
        if (Directory.Exists(inProgressDir))
            Directory.Delete(inProgressDir, recursive: true);
        Directory.CreateDirectory(inProgressDir);

        await tx.CommitAsync(cancel);

        return NoContent();
    }

    [HttpPost("file")]
    [RequestSizeLimit(2048L * 1024 * 1024)]
    public async Task<IActionResult> MultiPublishFile(
        [FromHeader(Name = "Robust-Cdn-Publish-File")]
        string fileName,
        [FromHeader(Name = "Robust-Cdn-Publish-Version")]
        string version,
        CancellationToken cancel)
    {
        if (!authHelper.IsAuthValid(out var failureResult))
            return failureResult;

        if (!ValidFileRegex.IsMatch(fileName))
            return BadRequest("Invalid artifact file name");

        var forkId = manifestDatabase.Context.Forks
            .Where(f => f.Name == ForkName)
            .Select(f => f.Id)
            .First();
        var versionId = manifestDatabase.Context.PublishInProgresses
            .Where(p => p.Version == version && p.ForkId == forkId)
            .Select(p => (int?)p.Id)
            .FirstOrDefault();

        if (versionId == null)
            return NotFound("Unknown in-progress version");

        var filePath = buildDirectoryManager.GetRobustInProgressPublishFilePath(version, fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);

        if (System.IO.File.Exists(filePath))
            System.IO.File.Delete(filePath);

        await using var file = System.IO.File.Create(filePath, 4096, FileOptions.Asynchronous);
        await Request.Body.CopyToAsync(file, cancel);

        return NoContent();
    }

    [HttpPost("finish")]
    public async Task<IActionResult> MultiPublishFinish(
        [FromBody] PublishFinishRequest request,
        CancellationToken cancel)
    {
        if (!authHelper.IsAuthValid(out var failureResult))
            return failureResult;

        var opts = robustOptions.Value;
        await using var tx = await manifestDatabase.Context.Database.BeginTransactionAsync(cancel);

        var forkId = manifestDatabase.Context.Forks
            .Where(f => f.Name == ForkName)
            .Select(f => f.Id)
            .First();
        var versionMetadata = manifestDatabase.Context.PublishInProgresses
            .Where(p => p.Version == request.Version && p.ForkId == forkId)
            .Select(p => new VersionMetadata { Version = p.Version, EngineVersion = p.EngineVersion })
            .FirstOrDefault();

        if (versionMetadata == null)
            return NotFound("Unknown in-progress version");

        var inProgressDir = buildDirectoryManager.GetRobustInProgressPublishPath(request.Version);
        var versionDir = buildDirectoryManager.GetRobustBuildVersionPath(request.Version);

        var artifacts = Directory.Exists(inProgressDir)
            ? Directory.GetFiles(inProgressDir)
            : [];

        var classified = ClassifyEntries(opts, artifacts, p => Path.GetFileName(p));
        var clientArtifacts = classified.Where(a => a.artifact.Type == ArtifactType.Client).ToList();
        if (clientArtifacts.Count == 0)
        {
            publishManager.AbortMultiPublish(ForkName, request.Version);
            await tx.CommitAsync(cancel);
            return UnprocessableEntity($"Publish failed: no client zip was provided. Expected '{opts.ClientZipName}.zip'.");
        }

        var primaryClient = clientArtifacts.FirstOrDefault(c => c.artifact.Platform == "win-x64");
        if (primaryClient.artifact == null)
            primaryClient = clientArtifacts.FirstOrDefault(c => c.artifact.Platform == null);
        if (primaryClient.artifact == null) primaryClient = clientArtifacts.First();
        var diskFiles = classified.ToDictionary(i => i.artifact, i => i.key);
        var clientByRid = clientArtifacts
            .Where(c => c.artifact.Platform != null)
            .ToDictionary(c => c.artifact.Platform!, c => c.artifact, StringComparer.Ordinal);

        InjectBuildJsonIntoServers(
            diskFiles,
            versionMetadata,
            serverArtifact =>
            {
                if (serverArtifact.Platform != null && clientByRid.TryGetValue(serverArtifact.Platform, out var ridClient))
                { return ridClient; }
                return primaryClient.artifact;
            });

        if (opts.AllowRepublish)
        {
            DeleteContentVersionIfExists(ForkName, request.Version);
            var oldVersions = manifestDatabase.Context.ForkVersions.Where(fv => fv.ForkId == forkId && fv.Name == request.Version);
            manifestDatabase.Context.ForkVersions.RemoveRange(oldVersions);
            await manifestDatabase.Context.SaveChangesAsync(cancel);
        }

        if (Directory.Exists(versionDir))
        {
            if (!opts.AllowRepublish)
                return Conflict("Version already exists");

            Directory.Delete(versionDir, recursive: true);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(versionDir)!);
        Directory.Move(inProgressDir, versionDir);

        var diskFilesFinal = diskFiles.ToDictionary(
            kvp => kvp.Key,
            kvp => Path.Combine(versionDir, Path.GetFileName(kvp.Value)));

        AddVersionToDatabase(primaryClient.artifact, diskFilesFinal, versionMetadata, forkId);

        var toDelete = manifestDatabase.Context.PublishInProgresses.Where(p => p.Version == request.Version && p.ForkId == forkId);
        manifestDatabase.Context.PublishInProgresses.RemoveRange(toDelete);
        await manifestDatabase.Context.SaveChangesAsync(cancel);

        await tx.CommitAsync(cancel);

        await QueueIngestJobAsync();

        return NoContent();
    }

    private async Task QueueIngestJobAsync()
    {
        var scheduler = await schedulerFactory.GetScheduler();
        await scheduler.TriggerJob(IngestNewCdnContentJob.Key, IngestNewCdnContentJob.Data(ForkName));
    }

    private void DeleteContentVersionIfExists(string fork, string version)
    {
        var toDelete = cdnDatabase.Context.ContentVersions
            .Where(cv => cv.Fork.Name == fork && cv.Version == version)
            .ToList();
        if (toDelete.Any())
        {
            cdnDatabase.Context.ContentVersions.RemoveRange(toDelete);
            cdnDatabase.Context.SaveChanges();
        }
    }

    private static List<(T key, Artifact artifact)> ClassifyEntries<T>(
        RobustOptions opts,
        IEnumerable<T> items,
        Func<T, string> getName)
    {
        var list = new List<(T, Artifact)>();
        foreach (var item in items)
        {
            var name = getName(item);
            var artifact = ClassifyEntry(opts, name);
            if (artifact == null)
                continue;
            list.Add((item, artifact));
        }
        return list;
    }

    private static Artifact? ClassifyEntry(RobustOptions opts, string name)
    {
        if (name == $"{opts.ClientZipName}.zip")
            return new Artifact { Type = ArtifactType.Client };

        var clientPrefix = $"{opts.ClientZipName}_";
        if (name.StartsWith(clientPrefix, StringComparison.Ordinal) && name.EndsWith(".zip", StringComparison.Ordinal))
        {
            var rid = name[clientPrefix.Length..^".zip".Length];
            if (rid.Length == 0) return null;
            return new Artifact { Type = ArtifactType.Client, Platform = rid };
        }

        if (name.StartsWith(opts.ServerZipName, StringComparison.Ordinal) && name.EndsWith(".zip", StringComparison.Ordinal))
        {
            var rid = name[opts.ServerZipName.Length..^".zip".Length];
            return new Artifact { Type = ArtifactType.Server, Platform = rid };
        }

        return null;
    }

    private MemoryStream GenerateBuildJson(
        Dictionary<Artifact, string> diskFiles,
        Artifact clientArtifact,
        VersionMetadata metadata)
    {
        var diskPath = diskFiles[clientArtifact];
        var diskFileName = Path.GetFileName(diskPath);
        using var file = System.IO.File.OpenRead(diskPath);

        var hash = Convert.ToHexString(SHA256.HashData(file));
        var manifestHash = Convert.ToHexString(GenerateManifestHash(file));

        var data = new Dictionary<string, string>
        {
            { "download", baseUrlManager.MakeBuildInfoUrl($"robust/version/{{FORK_VERSION}}/file/{diskFileName}") },
            { "version", metadata.Version },
            { "hash", hash },
            { "fork_id", ForkName },
            { "engine_version", metadata.EngineVersion },
            { "manifest_url", baseUrlManager.MakeBuildInfoUrl("robust/version/{FORK_VERSION}/manifest") },
            { "manifest_download_url", baseUrlManager.MakeBuildInfoUrl("robust/version/{FORK_VERSION}/download") },
            { "manifest_hash", manifestHash }
        };

        var stream = new MemoryStream();
        JsonSerializer.Serialize(stream, data);
        stream.Position = 0;
        return stream;
    }
    private byte[] GenerateManifestHash(Stream zipFile)
    {
        using var zip = new ZipArchive(zipFile, ZipArchiveMode.Read);
        var manifest = new MemoryStream();
        var writer = new StreamWriter(manifest, new UTF8Encoding(false), leaveOpen: true);
        writer.Write("Robust Content Manifest 1\n");
        foreach (var entry in zip.Entries.OrderBy(e => e.FullName, StringComparer.Ordinal))
        {
            if (entry.Name == "")
                continue;
            var hash = GetZipEntryBlake2B(entry);
            writer.Write($"{Convert.ToHexString(hash)} {entry.FullName}\n");
        }
        writer.Dispose();
        return CryptoGenericHashBlake2B.Hash(
            CryptoGenericHashBlake2B.Bytes,
            manifest.AsSpan(),
            ReadOnlySpan<byte>.Empty);
    }

    private static byte[] GetZipEntryBlake2B(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        return HashHelper.HashBlake2B(stream);
    }

    private void InjectBuildJsonIntoServers(Dictionary<Artifact, string> diskFiles, VersionMetadata metadata, Func<Artifact, Artifact> resolveClient)
    {
        foreach (var (artifact, diskPath) in diskFiles)
        {
            if (artifact.Type != ArtifactType.Server) continue;
            using var buildJson = GenerateBuildJson(diskFiles, resolveClient(artifact), metadata);
            using var zipFile = System.IO.File.Open(diskPath, FileMode.Open);
            using var zip = new ZipArchive(zipFile, ZipArchiveMode.Update);

            if (zip.GetEntry("build.json") is { } existing)
                existing.Delete();

            var buildJsonEntry = zip.CreateEntry("build.json");
            using var entryStream = buildJsonEntry.Open();

            buildJson.CopyTo(entryStream);
        }
    }

    private void AddVersionToDatabase(
        Artifact clientArtifact,
        Dictionary<Artifact, string> diskFiles,
        VersionMetadata metadata,
        int forkId)
    {
        var (clientName, clientSha256, _) = GetFileNameSha256Pair(diskFiles[clientArtifact]);

        var existingVersion = manifestDatabase.Context.ForkVersions
            .FirstOrDefault(fv => fv.ForkId == forkId && fv.Name == metadata.Version);

        if (existingVersion == null)
        {
            existingVersion = new ForkVersion
            {
                ForkId = forkId,
                Name = metadata.Version,
                PublishedTime = DateTime.UtcNow,
                ClientFileName = clientName,
                Sha256 = clientSha256,
                EngineVersion = metadata.EngineVersion,
                Available = false
            };
            manifestDatabase.Context.ForkVersions.Add(existingVersion);
        }
        else
        {
            existingVersion.PublishedTime = DateTime.UtcNow;
            existingVersion.ClientFileName = clientName;
            existingVersion.Sha256 = clientSha256;
            existingVersion.EngineVersion = metadata.EngineVersion;
            existingVersion.Available = false;
        }

        manifestDatabase.Context.SaveChanges();

        var versionId = existingVersion.Id;

        // Remove old server builds
        var oldBuilds = manifestDatabase.Context.ForkVersionServerBuilds.Where(fvsb => fvsb.ForkVersionId == versionId);
        manifestDatabase.Context.ForkVersionServerBuilds.RemoveRange(oldBuilds);

        foreach (var (artifact, diskPath) in diskFiles)
        {
            if (artifact.Type != ArtifactType.Server)
                continue;

            var (serverName, serverSha256, fileSize) = GetFileNameSha256Pair(diskPath);

            var serverBuild = new ForkVersionServerBuild
            {
                ForkVersionId = versionId,
                Platform = artifact.Platform,
                FileName = serverName,
                Sha256 = serverSha256,
                FileSize = (int?)fileSize
            };
            manifestDatabase.Context.ForkVersionServerBuilds.Add(serverBuild);
        }

        manifestDatabase.Context.SaveChanges();
    }

    private static (string name, byte[] hash, long size) GetFileNameSha256Pair(string diskPath)
    {
        using var file = System.IO.File.OpenRead(diskPath);
        return (Path.GetFileName(diskPath), SHA256.HashData(file), file.Length);
    }

    public sealed class PublishMultiRequest
    {
        public required string Version { get; set; }
        public required string EngineVersion { get; set; }
    }

    public sealed class PublishFinishRequest
    {
        public required string Version { get; set; }
    }

    private sealed class VersionMetadata
    {
        public required string Version { get; init; }
        public required string EngineVersion { get; set; }
    }

    [GeneratedRegex(@"[a-zA-Z0-9\-_][a-zA-Z0-9\-_.]*")]
    private static partial Regex ValidVersionRegexBuilder();

    [GeneratedRegex(@"[a-zA-Z0-9\-_][a-zA-Z0-9\-_.]*")]
    private static partial Regex ValidFileRegexBuilder();

    private sealed class Artifact
    {
        public ArtifactType Type { get; set; }
        public string? Platform { get; set; }
    }

    private enum ArtifactType
    {
        Server,
        Client
    }
}

