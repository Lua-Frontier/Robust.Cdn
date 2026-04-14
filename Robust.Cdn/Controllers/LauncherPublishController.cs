using System.Security.Cryptography;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Robust.Cdn.Config;
using Robust.Cdn.Helpers;

namespace Robust.Cdn.Controllers;

[ApiController]
[Route("/launcher/publish")]
public sealed partial class LauncherPublishController(LauncherAuthHelper authHelper, IOptionsSnapshot<LauncherOptions> options, BaseUrlManager baseUrlManager, ILogger<LauncherPublishController> logger) : ControllerBase
{
    private static readonly Regex ValidVersionRegex = ValidVersionRegexBuilder();
    private static readonly Regex ValidFileRegex = ValidFileRegexBuilder();
    private readonly LauncherOptions _options = options.Value;
    [HttpPost("start")]
    public IActionResult Start([FromBody] PublishStartRequest request)
    {
        if (!authHelper.IsAuthValid(out var failure)) return failure;
        if (string.IsNullOrWhiteSpace(_options.FileDiskPath)) return StatusCode(StatusCodes.Status500InternalServerError, "Launcher.FileDiskPath is not set");
        if (!ValidVersionRegex.IsMatch(request.Version)) return BadRequest("Invalid version name");
        if (!ValidFileRegex.IsMatch(request.LauncherFile)) return BadRequest("Invalid launcher file name");
        if (string.IsNullOrWhiteSpace(request.Rid)) return BadRequest("RID is required");
        var root = Path.GetFullPath(_options.FileDiskPath);
        Directory.CreateDirectory(root);
        var inProgress = GetInProgressDir(root, request.Version);
        if (Directory.Exists(inProgress))
        {
            logger.LogWarning("Already had an in-progress launcher publish for {Version}, deleting and restarting.", request.Version);
            Directory.Delete(inProgress, recursive: true);
        }
        Directory.CreateDirectory(inProgress);
        var metaPath = Path.Combine(inProgress, "meta.json");
        System.IO.File.WriteAllText(metaPath, JsonSerializer.Serialize(request, JsonOptions), Encoding.UTF8);
        logger.LogInformation("Launcher publish started for {Version} ({Rid})", request.Version, request.Rid);
        return NoContent();
    }

    [HttpPost("file")]
    [RequestSizeLimit(2048L * 1024 * 1024)]
    public async Task<IActionResult> File([FromHeader(Name = "Robust-Cdn-Publish-File")] string fileName, [FromHeader(Name = "Robust-Cdn-Publish-Version")] string version, CancellationToken cancel)
    {
        if (!authHelper.IsAuthValid(out var failure)) return failure;
        if (!ValidFileRegex.IsMatch(fileName)) return BadRequest("Invalid artifact file name");
        if (!ValidVersionRegex.IsMatch(version)) return BadRequest("Invalid version name");
        if (string.IsNullOrWhiteSpace(_options.FileDiskPath)) return StatusCode(StatusCodes.Status500InternalServerError, "Launcher.FileDiskPath is not set");
        var root = Path.GetFullPath(_options.FileDiskPath);
        var inProgress = GetInProgressDir(root, version);
        if (!Directory.Exists(inProgress)) return NotFound("Unknown in-progress version");
        var filePath = Path.Combine(inProgress, fileName);
        if (System.IO.File.Exists(filePath)) return Conflict("File already published");
        logger.LogDebug("Receiving launcher publish file {File} for version {Version}", fileName, version);
        await using var file = System.IO.File.Create(filePath, 4096, FileOptions.Asynchronous);
        await Request.Body.CopyToAsync(file, cancel);
        return NoContent();
    }

    [HttpPost("finish")]
    public async Task<IActionResult> Finish([FromBody] PublishFinishRequest request)
    {
        if (!authHelper.IsAuthValid(out var failure)) return failure;
        try
        { baseUrlManager.ValidateBaseUrl(); }
        catch (Exception e)
        { return StatusCode(StatusCodes.Status500InternalServerError, e.Message); }
        if (!ValidVersionRegex.IsMatch(request.Version)) return BadRequest("Invalid version name");
        if (string.IsNullOrWhiteSpace(_options.FileDiskPath)) return StatusCode(StatusCodes.Status500InternalServerError, "Launcher.FileDiskPath is not set");
        var root = Path.GetFullPath(_options.FileDiskPath);
        var inProgress = GetInProgressDir(root, request.Version);
        if (!Directory.Exists(inProgress)) return NotFound("Unknown in-progress version");
        var metaPath = Path.Combine(inProgress, "meta.json");
        if (!System.IO.File.Exists(metaPath)) return UnprocessableEntity("Missing meta.json, publish was not started correctly");
        var meta = JsonSerializer.Deserialize<PublishStartRequest>(System.IO.File.ReadAllText(metaPath, Encoding.UTF8), JsonOptions);
        if (meta == null) return UnprocessableEntity("Invalid meta.json");
        var launcherSrc = Path.Combine(inProgress, meta.LauncherFile);
        if (!System.IO.File.Exists(launcherSrc)) return UnprocessableEntity($"Missing required file: {meta.LauncherFile}");
        var launcherDst = Path.Combine(root, meta.LauncherFile);
        if (System.IO.File.Exists(launcherDst))
            System.IO.File.Delete(launcherDst);
        System.IO.File.Move(launcherSrc, launcherDst);
        string sha;
        await using (var fs = System.IO.File.OpenRead(launcherDst))
        { sha = Convert.ToHexString(SHA256.HashData(fs)); }
        var size = new FileInfo(launcherDst).Length;
        var url = baseUrlManager.MakeBuildInfoUrl($"launcher/files/{meta.LauncherFile}");
        var updatePath = Path.Combine(root, "update.json");
        var pkg = new UpdatePackage(meta.Rid, url, sha, size);
        var updated = MergeUpdateJson(updatePath, meta.Version, pkg, _options.CdnUrl);
        System.IO.File.WriteAllText(updatePath, JsonSerializer.Serialize(updated, JsonOptionsIndented), Encoding.UTF8);
        Directory.Delete(inProgress, recursive: true);
        logger.LogInformation("Launcher publish finished for {Version} ({Rid})", meta.Version, meta.Rid);
        return NoContent();
    }
    private static string GetInProgressDir(string root, string version) => Path.Combine(root, ".publish", version);
    private static readonly JsonSerializerOptions JsonOptions = new()
    { PropertyNameCaseInsensitive = true };
    private static readonly JsonSerializerOptions JsonOptionsIndented = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public sealed class PublishStartRequest
    {
        public required string Version { get; set; }
        public required string Rid { get; set; }
        public required string LauncherFile { get; set; }
    }

    public sealed class PublishFinishRequest
    { public required string Version { get; set; } }
    [GeneratedRegex(@"[a-zA-Z0-9\-_][a-zA-Z0-9\-_.]*")]
    private static partial Regex ValidVersionRegexBuilder();
    [GeneratedRegex(@"[a-zA-Z0-9\-_][a-zA-Z0-9\-_.]*")]
    private static partial Regex ValidFileRegexBuilder();
    private static UpdateManifest MergeUpdateJson(string updatePath, string version, UpdatePackage pkg, string cdnUrl)
    {
        UpdateManifest? existing = null;
        if (System.IO.File.Exists(updatePath))
        {
            try
            {
                var txt = System.IO.File.ReadAllText(updatePath, Encoding.UTF8);
                existing = JsonSerializer.Deserialize<UpdateManifest>(txt, JsonOptions);
            }
            catch
            {
                existing = null;
            }
        }

        var cdnUrlNorm = string.IsNullOrWhiteSpace(cdnUrl) ? null : cdnUrl.TrimEnd('/');

        if (existing == null || !string.Equals(existing.LatestVersion, version, StringComparison.Ordinal))
            return new UpdateManifest(version, new List<UpdatePackage> { pkg }, cdnUrlNorm);

        var list = existing.Packages ?? new List<UpdatePackage>();
        var idx = list.FindIndex(p => string.Equals(p.Rid, pkg.Rid, StringComparison.OrdinalIgnoreCase));
        if (idx >= 0) list[idx] = pkg;
        else list.Add(pkg);
        return new UpdateManifest(version, list, cdnUrlNorm);
    }

    public sealed class UpdateManifest
    {
        public string LatestVersion { get; init; } = "";
        public List<UpdatePackage> Packages { get; init; } = new();
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public string? CdnUrl { get; init; }
        public UpdateManifest() { }
        public UpdateManifest(string latestVersion, List<UpdatePackage> packages, string? cdnUrl)
        {
            LatestVersion = latestVersion;
            Packages = packages;
            CdnUrl = cdnUrl;
        }
    }

    public sealed record UpdatePackage(string Rid, string Url, string Sha256, long Size);
}

