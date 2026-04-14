using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dapper;
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

        var forkId = database.Connection.QuerySingle<int>(
            "SELECT Id FROM Fork WHERE Name = @ForkName",
            new { ForkName });

        logger.LogInformation("Updating manifest cache for robust");

        var data = CollectManifestData(forkId);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(data, ManifestCacheContext);

        database.Connection.Execute("UPDATE Fork SET ServerManifestCache = @Data WHERE Id = @ForkId",
            new { Data = bytes, ForkId = forkId });
        return Task.CompletedTask;
    }

    private ManifestData CollectManifestData(int forkId)
    {
        var opts = robustOptions.Value;
        var data = new ManifestData { Builds = new Dictionary<string, ManifestBuildData>() };

        var versions = database.Connection
            .Query<(int id, string name, DateTime time, string clientFileName, byte[] clientSha256)>(
                """
                SELECT Id, Name, PublishedTime, ClientFileName, ClientSha256
                FROM ForkVersion
                WHERE Available AND ForkId = @ForkId
                """,
                new { ForkId = forkId });

        foreach (var version in versions)
        {
            var platforms = opts.IncludeClientPlatformsInManifest
                ? CollectClientPlatforms(version.name, opts.ClientZipName, version.clientFileName, version.clientSha256)
                : null;

            var buildData = new ManifestBuildData
            {
                Time = DateTime.SpecifyKind(version.time, DateTimeKind.Utc),
                Client = new ManifestArtifact
                {
                    Url = baseUrlManager.MakeBuildInfoUrl(
                        $"robust/version/{version.name}/file/{version.clientFileName}"),
                    Sha256 = Convert.ToHexString(version.clientSha256)
                },
                Platforms = platforms is { Count: > 0 } ? platforms : null,
                Server = new Dictionary<string, ManifestArtifact>()
            };

            var servers = database.Connection.Query<(string platform, string fileName, byte[] sha256, long? size)>("""
                SELECT Platform, FileName, Sha256, FileSize
                FROM ForkVersionServerBuild
                WHERE ForkVersionId = @ForkVersionId
                """, new { ForkVersionId = version.id });

            foreach (var (platform, fileName, sha256, size) in servers)
            {
                buildData.Server.Add(platform, new ManifestArtifact
                {
                    Url = baseUrlManager.MakeBuildInfoUrl($"robust/version/{version.name}/file/{fileName}"),
                    Sha256 = Convert.ToHexString(sha256),
                    Size = size
                });
            }

            data.Builds.Add(version.name, buildData);
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

