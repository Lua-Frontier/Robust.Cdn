using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Quartz;
using Robust.Cdn.Config;
using Robust.Cdn.Services;

namespace Robust.Cdn.Jobs;

/// <summary>
/// Job that periodically goes through and deletes old in-progress publishes that have "timed out".
/// </summary>
/// <remarks>
/// This deletes old in-progress publishes that have taken too long since being initiated,
/// which likely indicates that the publish encountered an error and will never be completed.
/// </remarks>
/// <seealso cref="ManifestOptions.InProgressPublishTimeoutMinutes"/>
public sealed class DeleteInProgressPublishesJob(
    PublishManager publishManager,
    ManifestDatabase manifestDatabase,
    TimeProvider timeProvider,
    IOptions<ManifestOptions> options,
    ILogger<DeleteInProgressPublishesJob> logger) : IJob
{
    public Task Execute(IJobExecutionContext context)
    {
        var opts = options.Value;

        logger.LogTrace("Checking for timed out in-progress publishes");

        var deleteBefore = timeProvider.GetUtcNow() - TimeSpan.FromMinutes(opts.InProgressPublishTimeoutMinutes);

        var totalDeleted = 0;

        var inProgress = manifestDatabase.Context.PublishInProgresses
            .AsNoTracking()
            .Select(p => new { p.Id, p.Version, ForkName = p.Fork.Name, p.StartTime })
            .ToList();

        foreach (var item in inProgress)
        {
            if (item.StartTime >= deleteBefore)
                continue;

            logger.LogInformation("Deleting timed out publish for fork {Fork} version {Version}", item.ForkName, item.Version);

            publishManager.AbortMultiPublish(item.ForkName, item.Version);

            totalDeleted += 1;
        }

        logger.LogInformation("Deleted {TotalDeleted} timed out publishes", totalDeleted);

        return Task.CompletedTask;
    }
}
