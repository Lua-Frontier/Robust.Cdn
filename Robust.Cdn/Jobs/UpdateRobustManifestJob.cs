using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Quartz;
using Robust.Cdn.Config;
using Robust.Cdn.Helpers;

namespace Robust.Cdn.Jobs;
public sealed class UpdateRobustManifestJob(
    ManifestDatabase database,
    BaseUrlManager baseUrlManager,
    BuildDirectoryManager buildDirectoryManager,
    IOptions<RobustOptions> robustOptions,
    ILogger<UpdateRobustManifestJob> logger) : IJob
{
    private static readonly JsonSerializerOptions ManifestCacheContext = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
    public static readonly JobKey Key = new(nameof(UpdateRobustManifestJob));
    public const string ForkName = "robust";
    public Task Execute(IJobExecutionContext context)
    {
        _ = context;

        var forkId = database.Context.Forks
            .Where(f => f.Name == ForkName)
            .Select(f => f.Id)
            .First();

        logger.LogInformation("Updating manifest cache for robust");

        var data = CollectManifestData(forkId);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(data, ManifestCacheContext);

        var fork = database.Context.Forks.Find(forkId);
        if (fork is null)
            throw new InvalidOperationException($"Fork with id {forkId} not found.");

        fork.ServerManifestCache = bytes;
        database.Context.SaveChanges();
        return Task.CompletedTask;
    }

    private ManifestData CollectManifestData(int forkId)
    {
        var opts = robustOptions.Value;
        var data = new ManifestData { Builds = new Dictionary<string, ManifestBuildData>() };

        var versions = database.Context.ForkVersions
            .AsNoTracking()
            .Where(v => v.Available && v.ForkId == forkId)
            .Select(v => new { v.Id, v.Name, v.PublishedTime, v.ClientFileName, v.Sha256 })
            .ToList();

        foreach (var version in versions)
        {
            var platforms = opts.IncludeClientPlatformsInManifest
                ? CollectClientPlatforms(version.Name, opts.ClientZipName, version.ClientFileName, version.Sha256)
                : null;

            var buildData = new ManifestBuildData
            {
                Time = DateTime.SpecifyKind(version.PublishedTime, DateTimeKind.Utc),
                Client = new ManifestArtifact
                {
                    Url = baseUrlManager.MakeBuildInfoUrl(
                        $"robust/version/{version.Name}/file/{version.ClientFileName}"),
                    Sha256 = Convert.ToHexString(version.Sha256)
                },
                Platforms = platforms is { Count: > 0 } ? platforms : null,
                Server = new Dictionary<string, ManifestArtifact>()
            };

            var servers = database.Context.ForkVersionServerBuilds
                .AsNoTracking()
                .Where(s => s.ForkVersionId == version.Id)
                .Select(s => new { s.Platform, s.FileName, s.Sha256, s.FileSize })
                .ToList();

            foreach (var server in servers)
            {
                buildData.Server.Add(server.Platform, new ManifestArtifact
                {
                    Url = baseUrlManager.MakeBuildInfoUrl($"robust/version/{version.Name}/file/{server.FileName}"),
                    Sha256 = Convert.ToHexString(server.Sha256),
                    Size = server.FileSize
                });
            }

            data.Builds.Add(version.Name, buildData);
        }

        return data;
    }

    private Dictionary<string, ManifestArtifact> CollectClientPlatforms(
        string version,
        string clientZipName,
        string primaryClientFileName,
        byte[] primaryClientSha256)
    {
        var result = new Dictionary<string, ManifestArtifact>(StringComparer.Ordinal);
        var versionDir = buildDirectoryManager.GetRobustBuildVersionPath(version);
        if (!Directory.Exists(versionDir))
            return result;

        var clientBase = GetClientZipBaseName(clientZipName);
        foreach (var filePath in Directory.EnumerateFiles(versionDir, clientBase + "*.zip"))
        {
            var fileName = Path.GetFileName(filePath);

            if (!fileName.StartsWith(clientBase, StringComparison.Ordinal) || !fileName.EndsWith(".zip", StringComparison.Ordinal))
                continue;

            string? platform = null;
            if (fileName.StartsWith(clientBase + "_", StringComparison.Ordinal))
            {
                platform = fileName[(clientBase.Length + 1)..^".zip".Length];
                if (platform.Length == 0)
                    platform = null;
            }

            if (platform == null)
                continue;

            var sha256 = fileName == primaryClientFileName ? primaryClientSha256 : ComputeSha256(filePath);
            result[platform] = new ManifestArtifact
            {
                Url = baseUrlManager.MakeBuildInfoUrl($"robust/version/{version}/file/{fileName}"),
                Sha256 = Convert.ToHexString(sha256),
            };
        }

        return result;
    }

    private static string GetClientZipBaseName(string configuredName)
    {
        var idx = configuredName.LastIndexOf('_');
        return idx > 0 ? configuredName[..idx] : configuredName;
    }

    private static byte[] ComputeSha256(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        return SHA256.HashData(stream);
    }

    private sealed class ManifestData
    {
        public required Dictionary<string, ManifestBuildData> Builds { get; set; }
    }

    private sealed class ManifestBuildData
    {
        public DateTime Time { get; set; }
        public required ManifestArtifact Client { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public Dictionary<string, ManifestArtifact>? Platforms { get; set; }
        public required Dictionary<string, ManifestArtifact> Server { get; set; }
    }

    private sealed class ManifestArtifact
    {
        public required string Url { get; set; }
        public required string Sha256 { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public long? Size { get; set; }
    }
}

