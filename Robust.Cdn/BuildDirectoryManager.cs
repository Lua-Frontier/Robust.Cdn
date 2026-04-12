using Microsoft.Extensions.Options;
using Robust.Cdn.Config;

namespace Robust.Cdn;

/// <summary>
/// Manages storage for manifest server builds.
/// </summary>
/// <remarks>
/// For now this takes care of all the "<c>Path.Combine</c>" calls in the project.
/// In the future this should be expanded to other file access methods like cloud storage, if we want those.
/// </remarks>
public sealed class BuildDirectoryManager(
    IOptions<ManifestOptions> options,
    IOptions<RobustOptions> robustOptions)
{
    public string GetForkPath(string fork)
    {
        var opts = options.Value;
        if (opts.Forks.TryGetValue(fork, out var forkOptions)
            && !string.IsNullOrWhiteSpace(forkOptions.FileDiskPath))
        {
            return Path.GetFullPath(forkOptions.FileDiskPath);
        }

        return Path.Combine(Path.GetFullPath(opts.FileDiskPath), fork);
    }
    public string GetRobustPath()
    {
        return Path.GetFullPath(robustOptions.Value.FileDiskPath);
    }

    public string GetBuildVersionPath(string fork, string version)
    {
        return Path.Combine(GetForkPath(fork), version);
    }

    public string GetBuildVersionFilePath(string fork, string version, string file)
    {
        return Path.Combine(GetBuildVersionPath(fork, version), file);
    }

    public string GetInProgressPublishPath(string fork, string version)
    {
        return Path.Combine(GetForkPath(fork), ".publish", version);
    }

    public string GetInProgressPublishFilePath(string fork, string version, string file)
    {
        return Path.Combine(GetInProgressPublishPath(fork, version), file);
    }

    public string GetRobustBuildVersionPath(string version)
    {
        return Path.Combine(GetRobustPath(), version);
    }

    public string GetRobustBuildVersionFilePath(string version, string file)
    {
        return Path.Combine(GetRobustBuildVersionPath(version), file);
    }

    public string GetRobustInProgressPublishPath(string version)
    {
        return Path.Combine(GetRobustPath(), ".publish", version);
    }

    public string GetRobustInProgressPublishFilePath(string version, string file)
    {
        return Path.Combine(GetRobustInProgressPublishPath(version), file);
    }
}
