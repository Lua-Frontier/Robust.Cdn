using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Robust.Cdn.Config;
using Robust.Cdn.Helpers;
using Robust.Cdn.Jobs;
using System.Diagnostics.CodeAnalysis;
using System.Net.Mime;

namespace Robust.Cdn.Controllers;

[ApiController]
[Route("/robust")]
public sealed class RobustManifestController(
    ManifestDatabase database,
    BuildDirectoryManager buildDirectoryManager,
    IOptions<RobustOptions> robustOptions)
    : ControllerBase
{
    [HttpGet("manifest")]
    public IActionResult GetManifest()
    {
        if (!TryCheckBasicAuth(out var errorResult))
            return errorResult;

        const string fork = "robust";
        var data = database.Context.Forks
            .Where(f => f.Name == fork)
            .Select(f => f.ServerManifestCache)
            .FirstOrDefault();

        if (data == null)
            return NotFound();

        return File(new MemoryStream(data), MediaTypeNames.Application.Json);
    }

    [HttpGet("version/{version}/file/{file}")]
    public IActionResult GetFile(string version, string file)
    {
        if (file.Contains('/') || file == ".." || file == ".")
            return BadRequest();

        if (!TryCheckBasicAuth(out var errorResult))
            return errorResult;

        var versionExists = database.Context.ForkVersions
            .Any(v => v.Name == version && v.Fork.Name == UpdateRobustManifestJob.ForkName);

        if (!versionExists)
            return NotFound();

        var disk = buildDirectoryManager.GetRobustBuildVersionFilePath(version, file);
        return PhysicalFile(disk, MediaTypeNames.Application.Zip);
    }

    private bool TryCheckBasicAuth([NotNullWhen(false)] out IActionResult? errorResult)
    {
        var opts = robustOptions.Value;
        if (!opts.Private)
        {
            errorResult = null;
            return true;
        }
        return AuthorizationUtility.CheckBasicAuth(
            HttpContext,
            "robust",
            a => opts.PrivateUsers.GetValueOrDefault(a),
            out _,
            out errorResult);
    }
}

