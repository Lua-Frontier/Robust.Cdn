using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc;
using Robust.Cdn.Helpers;

namespace Robust.Cdn.Controllers;

public sealed partial class ForkPublishController
{
    // Code for "multi-request" publishes.
    // i.e. start, followed by files, followed by finish call.

    [HttpPost("start")]
    public async Task<IActionResult> MultiPublishStart(
        string fork,
        [FromBody] PublishMultiRequest request,
        CancellationToken cancel)
    {
        if (!authHelper.IsAuthValid(fork, out var forkConfig, out var failureResult))
            return failureResult;

        baseUrlManager.ValidateBaseUrl();

        if (!ValidVersionRegex.IsMatch(request.Version))
            return BadRequest("Invalid version name");

        var versionExists = VersionAlreadyExists(fork, request.Version);
        if (versionExists && !forkConfig.AllowRepublish)
            return Conflict("Version already exists");

        await using var tx = await manifestDatabase.Context.Database.BeginTransactionAsync(cancel);

        logger.LogInformation("Starting multi publish for fork {Fork} version {Version}", fork, request.Version);

        var forkId = manifestDatabase.Context.Forks
            .Where(f => f.Name == fork)
            .Select(f => f.Id)
            .First();
        var hasExistingPublish = manifestDatabase.Context.PublishInProgresses
            .Any(p => p.Version == request.Version && p.ForkId == forkId);
        if (hasExistingPublish)
        {
            // If a publish with this name already exists we abort it and start again.
            // We do this so you can "just" retry a mid-way-failed publish without an extra API call required.

            logger.LogWarning("Already had an in-progress publish for this version, aborting it and restarting.");
            publishManager.AbortMultiPublish(fork, request.Version);
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
        var inProgressDir = buildDirectoryManager.GetInProgressPublishPath(fork, request.Version);
        if (Directory.Exists(inProgressDir))
            Directory.Delete(inProgressDir, recursive: true);
        Directory.CreateDirectory(inProgressDir);

        await tx.CommitAsync(cancel);

        logger.LogInformation("Multi publish initiated. Waiting for subsequent API requests...");

        return NoContent();
    }

    [HttpPost("file")]
    [RequestSizeLimit(2048L * 1024 * 1024)]
    public async Task<IActionResult> MultiPublishFile(
        string fork,
        [FromHeader(Name = "Robust-Cdn-Publish-File")]
        string fileName,
        [FromHeader(Name = "Robust-Cdn-Publish-Version")]
        string version,
        CancellationToken cancel)
    {
        if (!authHelper.IsAuthValid(fork, out _, out var failureResult))
            return failureResult;

        if (!ValidFileRegex.IsMatch(fileName))
            return BadRequest("Invalid artifact file name");

        var forkId = manifestDatabase.Context.Forks
            .Where(f => f.Name == fork)
            .Select(f => f.Id)
            .First();
        var versionId = manifestDatabase.Context.PublishInProgresses
            .Where(p => p.Version == version && p.ForkId == forkId)
            .Select(p => (int?)p.Id)
            .FirstOrDefault();

        if (versionId == null)
            return NotFound("Unknown in-progress version");

        var inProgressDir = buildDirectoryManager.GetInProgressPublishPath(fork, version);
        Directory.CreateDirectory(inProgressDir);
        var filePath = buildDirectoryManager.GetInProgressPublishFilePath(fork, version, fileName);

        if (System.IO.File.Exists(filePath))
            System.IO.File.Delete(filePath);

        logger.LogDebug("Receiving file {FileName} for multi-publish version {Version}", fileName, version);

        await using var file = System.IO.File.Create(filePath, 4096, FileOptions.Asynchronous);

        await Request.Body.CopyToAsync(file, cancel);

        logger.LogDebug("Successfully Received file {FileName}", fileName);

        return NoContent();
    }

    [HttpPost("finish")]
    public async Task<IActionResult> MultiPublishFinish(
        string fork,
        [FromBody] PublishFinishRequest request,
        CancellationToken cancel)
    {
        if (!authHelper.IsAuthValid(fork, out var forkConfig, out var failureResult))
            return failureResult;

        await using var tx = await manifestDatabase.Context.Database.BeginTransactionAsync(cancel);

        var forkId = manifestDatabase.Context.Forks
            .Where(f => f.Name == fork)
            .Select(f => f.Id)
            .First();
        var versionMetadata = manifestDatabase.Context.PublishInProgresses
            .Where(p => p.Version == request.Version && p.ForkId == forkId)
            .Select(p => new VersionMetadata { Version = p.Version, EngineVersion = p.EngineVersion })
            .FirstOrDefault();

        if (versionMetadata == null)
            return NotFound("Unknown in-progress version");

        logger.LogInformation("Finishing multi publish {Version} for fork {Fork}", request.Version, fork);

        var inProgressDir = buildDirectoryManager.GetInProgressPublishPath(fork, request.Version);
        var versionDir = buildDirectoryManager.GetBuildVersionPath(fork, request.Version);
        var expectedClientZip = $"{forkConfig.ClientZipName}.zip";

        logger.LogDebug("Classifying entries...");

        var artifacts = ClassifyEntries(
            forkConfig,
            Directory.Exists(inProgressDir) ? Directory.GetFiles(inProgressDir) : [],
            item => Path.GetRelativePath(inProgressDir, item));

        var clientArtifacts = artifacts.Where(art => art.artifact.Type == ArtifactType.Client).ToList();
        if (clientArtifacts.Count == 0)
        {
            publishManager.AbortMultiPublish(fork, request.Version);
            await tx.CommitAsync(cancel);
            var diskFileNames = Directory.Exists(inProgressDir)
                ? Directory.GetFiles(inProgressDir).Select(Path.GetFileName)
                : [];

            return UnprocessableEntity(
                $"Publish failed: no client zip was provided. Expected '{expectedClientZip}'. " +
                $"Files on disk: {string.Join(", ", diskFileNames)}");
        }

        var primaryClient = clientArtifacts.FirstOrDefault(c => c.artifact.Platform == "win-x64");
        if (primaryClient.artifact == null) primaryClient = clientArtifacts.FirstOrDefault(c => c.artifact.Platform == null);
        if (primaryClient.artifact == null) primaryClient = clientArtifacts.First();
        var diskFiles = artifacts.ToDictionary(i => i.artifact, i => i.key);
        var clientByRid = clientArtifacts
            .Where(c => c.artifact.Platform != null)
            .ToDictionary(c => c.artifact.Platform!, c => c.artifact, StringComparer.Ordinal);
        InjectBuildJsonIntoServers(
            diskFiles,
            versionMetadata,
            fork,
            serverArtifact =>
            {
                if (serverArtifact.Platform != null &&
                    clientByRid.TryGetValue(serverArtifact.Platform, out var ridClient))
                {
                    return ridClient;
                }
                return primaryClient.artifact;
            });
        if (forkConfig.AllowRepublish)
            DeleteContentVersionIfExists(fork, request.Version);
        if (Directory.Exists(versionDir))
        {
            if (!forkConfig.AllowRepublish)
                return Conflict("Version already exists");

            Directory.Delete(versionDir, recursive: true);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(versionDir)!);
        Directory.Move(inProgressDir, versionDir);
        var diskFilesFinal = diskFiles.ToDictionary(
            kvp => kvp.Key,
            kvp => Path.Combine(versionDir, Path.GetFileName(kvp.Value)));

        AddVersionToDatabase(primaryClient.artifact, diskFilesFinal, fork, versionMetadata);

        var toDelete = manifestDatabase.Context.PublishInProgresses.Where(p => p.Version == request.Version && p.ForkId == forkId);
        manifestDatabase.Context.PublishInProgresses.RemoveRange(toDelete);
        await manifestDatabase.Context.SaveChangesAsync(cancel);

        await tx.CommitAsync(cancel);

        await QueueIngestJobAsync(fork);

        logger.LogInformation("Publish succeeded!");

        return NoContent();
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
}
