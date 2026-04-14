using Dapper;
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

        var rowId = database.Connection.QuerySingleOrDefault<long>(
            "SELECT ROWID FROM Fork WHERE Name == @Fork AND ServerManifestCache IS NOT NULL",
            new { Fork = UpdateRobustManifestJob.ForkName });

        if (rowId == 0)
            return NotFound();

        var stream = SqliteBlobStream.Open(
            database.Connection.Handle!,
            "main",
            "Fork",
            "ServerManifestCache",
            rowId,
            false);

        return File(stream, MediaTypeNames.Application.Json);
    }

    [HttpGet("version/{version}/file/{file}")]
    public IActionResult GetFile(string version, string file)
    {
        if (file.Contains('/') || file == ".." || file == ".")
            return BadRequest();

        if (!TryCheckBasicAuth(out var errorResult))
            return errorResult;

        var versionExists = database.Connection.QuerySingleOrDefault<bool>("""
            SELECT 1
            FROM ForkVersion, Fork
            WHERE ForkVersion.Name = @Version
              AND Fork.Name = @Fork
              AND Fork.Id = ForkVersion.ForkId
            """, new { Fork = UpdateRobustManifestJob.ForkName, Version = version });

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

