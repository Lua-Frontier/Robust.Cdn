using System.Diagnostics.CodeAnalysis;
using System.Net.Mime;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Robust.Cdn.Config;
using Robust.Cdn.Helpers;

namespace Robust.Cdn.Controllers;

[ApiController]
[Route("/launcher")]
public sealed class LauncherController(IOptionsSnapshot<LauncherOptions> options) : ControllerBase
{
    private readonly LauncherOptions _options = options.Value;

    [HttpGet("update.json")]
    public IActionResult GetUpdateManifest()
    {
        if (!TryCheckBasicAuth(out var error)) return error;
        if (string.IsNullOrWhiteSpace(_options.FileDiskPath)) return NotFound();
        var root = Path.GetFullPath(_options.FileDiskPath);
        var path = Path.Combine(root, "update.json");
        if (!System.IO.File.Exists(path)) return NotFound();
        Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
        Response.Headers.Pragma = "no-cache";
        Response.Headers.Expires = "0";
        return PhysicalFile(path, MediaTypeNames.Application.Json);
    }
    [HttpGet("files/{file}")]
    public IActionResult GetFile(string file)
    {
        if (file.Contains('/') || file.Contains('\\') || file is "." or "..") return BadRequest();
        if (!TryCheckBasicAuth(out var error)) return error;
        if (string.IsNullOrWhiteSpace(_options.FileDiskPath)) return NotFound();
        var root = Path.GetFullPath(_options.FileDiskPath);
        var path = Path.Combine(root, file);
        if (!System.IO.File.Exists(path)) return NotFound();
        Response.Headers.CacheControl = "public, max-age=31536000, immutable";
        return PhysicalFile(path, MediaTypeNames.Application.Octet);
    }

    private bool TryCheckBasicAuth([NotNullWhen(false)] out IActionResult? errorResult)
    {
        if (_options.PrivateUsers.Count == 0)
        {
            errorResult = null;
            return true;
        }
        return AuthorizationUtility.CheckBasicAuth(HttpContext, "launcher", user => _options.PrivateUsers.GetValueOrDefault(user), out _, out errorResult);
    }
}

