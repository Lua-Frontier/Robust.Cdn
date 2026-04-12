using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Robust.Cdn.Config;
using Robust.Cdn.Helpers;

namespace Robust.Cdn.Controllers;

[ApiController]
[Route("/launcher/publish")]
public sealed class LauncherBootstrapperPublishController(
    LauncherAuthHelper authHelper,
    IOptionsSnapshot<LauncherOptions> options,
    ILogger<LauncherBootstrapperPublishController> logger) : ControllerBase
{
    private readonly LauncherOptions _options = options.Value;
    [HttpPost("bootstrapper")]
    [RequestSizeLimit(512L * 1024 * 1024)]
    public async Task<IActionResult> UploadBootstrapper(
        [FromHeader(Name = "Robust-Cdn-Publish-File")] string fileName,
        CancellationToken cancel)
    {
        if (!authHelper.IsAuthValid(out var failure)) return failure;
        if (string.IsNullOrWhiteSpace(_options.FileDiskPath)) return StatusCode(StatusCodes.Status500InternalServerError, "Launcher.FileDiskPath is not set");
        if (string.IsNullOrWhiteSpace(fileName) || fileName.Contains('/') || fileName.Contains('\\') || fileName is "." or "..") return BadRequest("Invalid file name");
        var root = Path.GetFullPath(_options.FileDiskPath);
        Directory.CreateDirectory(root);
        var finalPath = Path.Combine(root, fileName);
        var tmpPath = Path.Combine(root, $".tmp-{Guid.NewGuid():N}-{fileName}");
        logger.LogInformation("Receiving bootstrapper file {FileName}", fileName);
        await using (var tmp = System.IO.File.Create(tmpPath, 4096, FileOptions.Asynchronous))
        { await Request.Body.CopyToAsync(tmp, cancel); }
        if (System.IO.File.Exists(finalPath)) System.IO.File.Delete(finalPath);
        System.IO.File.Move(tmpPath, finalPath);
        return NoContent();
    }
}

