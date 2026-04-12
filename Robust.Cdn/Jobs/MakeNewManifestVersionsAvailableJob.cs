using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Quartz;
using Robust.Cdn.Helpers;

namespace Robust.Cdn.Jobs;

public sealed class MakeNewManifestVersionsAvailableJob(
    ManifestDatabase database,
    ISchedulerFactory factory,
    ILogger<MakeNewManifestVersionsAvailableJob> logger) : IJob
{
    private static readonly JsonSerializerOptions ManifestCacheContext = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static readonly JobKey Key = new(nameof(MakeNewManifestVersionsAvailableJob));

    public const string KeyForkName = "ForkName";
    public const string KeyVersions = "Versions";

    public static JobDataMap Data(string fork, IEnumerable<string> versions) => new()
    {
        { KeyForkName, fork },
        { KeyVersions, versions.ToArray() },
    };

    public async Task Execute(IJobExecutionContext context)
    {
        var fork = context.MergedJobDataMap.GetString(KeyForkName) ?? throw new InvalidDataException();
        var versions = (string[])context.MergedJobDataMap.Get(KeyVersions) ?? throw new InvalidDataException();

        logger.LogInformation(
            "Updating version availability for manifest fork {Fork}, {VersionCount} new versions",
            fork,
            versions.Length);

        var forkId = database.Context.Forks
            .Where(f => f.Name == fork)
            .Select(f => f.Id)
            .First();

        MakeVersionsAvailable(forkId, versions);

        var scheduler = await factory.GetScheduler();
        if (fork == UpdateRobustManifestJob.ForkName)
        {
            await scheduler.TriggerJob(UpdateRobustManifestJob.Key);
        }
        else
        {
            await scheduler.TriggerJob(
                UpdateForkManifestJob.Key,
                UpdateForkManifestJob.Data(fork, notifyUpdate: true));
        }
    }

    private void MakeVersionsAvailable(int forkId, IEnumerable<string> versions)
    {
        foreach (var version in versions)
        {
            logger.LogInformation("New available version: {Version}", version);

            var versionEntity = database.Context.ForkVersions
                .FirstOrDefault(v => v.Name == version && v.ForkId == forkId);
            if (versionEntity != null)
            {
                versionEntity.Available = true;
                database.Context.SaveChanges();
            }
        }
    }
}
