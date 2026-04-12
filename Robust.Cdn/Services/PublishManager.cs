namespace Robust.Cdn.Services;

public sealed class PublishManager(
    ManifestDatabase manifestDatabase,
    BuildDirectoryManager buildDirectoryManager,
    ILogger<PublishManager> logger)
{
    private const string RobustForkName = "robust";

    public void AbortMultiPublish(string fork, string version)
    {
        logger.LogDebug("Aborting publish for fork {Fork}, version {version}", fork, version);

        var db = manifestDatabase.Context;
        var toDelete = db.PublishInProgresses
            .Where(p => p.Version == version && p.Fork.Name == fork)
            .ToList();

        db.PublishInProgresses.RemoveRange(toDelete);
        db.SaveChanges();

        var versionDir = fork == RobustForkName
            ? buildDirectoryManager.GetRobustInProgressPublishPath(version)
            : buildDirectoryManager.GetInProgressPublishPath(fork, version);
        if (Directory.Exists(versionDir))
            Directory.Delete(versionDir, recursive: true);
    }
}
