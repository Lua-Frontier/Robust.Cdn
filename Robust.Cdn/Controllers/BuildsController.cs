using System.Net.Mime;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Robust.Cdn.Config;

namespace Robust.Cdn.Controllers;

[ApiController]
public sealed class BuildsController(
    IOptionsSnapshot<CdnOptions> cdnOptions,
    IOptionsSnapshot<ManifestOptions> manifestOptions,
    BuildDirectoryManager buildDirectoryManager) : ControllerBase
{
    [HttpGet("/builds/{version}/{file}")]
    public IActionResult GetBuildFile(string version, string file)
    {
        if (version.Contains('/') || version is "." or "..") return BadRequest();
        if (file.Contains('/') || file is "." or "..") return BadRequest();
        var candidates = new List<string>();

        var defaultFork = cdnOptions.Value.DefaultFork;
        if (!string.IsNullOrWhiteSpace(defaultFork))
            candidates.Add(defaultFork);

        foreach (var (fork, cfg) in manifestOptions.Value.Forks)
        {
            if (cfg.Private)
                continue;

            if (!candidates.Contains(fork))
                candidates.Add(fork);
        }

        foreach (var fork in candidates)
        {
            var disk = buildDirectoryManager.GetBuildVersionFilePath(fork, version, file);
            if (System.IO.File.Exists(disk))
                return PhysicalFile(disk, MediaTypeNames.Application.Zip);
        }

        return NotFound();
    }
}

