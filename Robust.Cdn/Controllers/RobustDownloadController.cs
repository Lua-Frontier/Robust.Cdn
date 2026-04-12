using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Robust.Cdn.Config;
using Robust.Cdn.Services;

namespace Robust.Cdn.Controllers;
[ApiController]
[Route("/robust/version/{version}")]
public sealed class RobustDownloadController(Database db, ILogger<DownloadController> logger, IOptionsSnapshot<CdnOptions> options, DownloadRequestLogger requestLogger) : DownloadController(db, logger, options, requestLogger)
{
    [HttpGet("manifest")]
    public IActionResult GetManifest(string version) => base.GetManifest("robust", version);
    [HttpOptions("download")]
    public IActionResult DownloadOptions(string version) => base.DownloadOptions("robust", version);
    [HttpPost("download")]
    public Task<IActionResult> Download(string version) => base.Download("robust", version);
}

