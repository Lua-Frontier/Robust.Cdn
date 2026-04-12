using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Robust.Cdn.Config;
using Robust.Cdn.Helpers;
using SpaceWizards.Sodium;

namespace Robust.Cdn.Controllers;

[ApiController]
[Route("/launcher/version/{version}/{rid}")]
public sealed class LauncherVersionController(
    IOptionsSnapshot<LauncherOptions> options,
    ILogger<LauncherVersionController> logger) : ControllerBase
{
    private readonly LauncherOptions _options = options.Value;

    [HttpGet("manifest")]
    public IActionResult GetManifest(string version, string rid)
    {
        var zipPath = ResolveZipPath(version, rid);
        if (zipPath is null) return NotFound();

        var sb = new StringBuilder();
        sb.Append("Robust Content Manifest 1\n");

        using var zip = ZipFile.OpenRead(zipPath);
        foreach (var entry in zip.Entries.OrderBy(e => e.FullName, StringComparer.Ordinal))
        {
            if (entry.Name == "") continue; // directory entry

            using var stream = entry.Open();
            var hash = HashHelper.HashBlake2B(stream);
            sb.Append(Convert.ToHexString(hash));
            sb.Append(' ');
            sb.Append(entry.FullName);
            sb.Append('\n');
        }

        return Content(sb.ToString(), "text/plain; charset=utf-8");
    }

    [HttpPost("download")]
    public async Task<IActionResult> Download(string version, string rid, CancellationToken cancel)
    {
        if (Request.ContentType != "application/octet-stream")
            return BadRequest("Must specify application/octet-stream Content-Type");

        if (Request.Headers["X-Robust-Download-Protocol"] != "1")
            return BadRequest("Unknown X-Robust-Download-Protocol");

        var zipPath = ResolveZipPath(version, rid);
        if (zipPath is null) return NotFound();
        using var bodyBuf = new MemoryStream();
        await Request.Body.CopyToAsync(bodyBuf, cancel);
        var body = bodyBuf.GetBuffer().AsMemory(0, (int)bodyBuf.Position);

        if (body.Length % 4 != 0)
            return BadRequest("Body must be a multiple of 4 bytes");

        var requestedIndices = new List<int>(body.Length / 4);
        for (var i = 0; i < body.Length; i += 4)
        {
            var idx = BinaryPrimitives.ReadInt32LittleEndian(body.Slice(i, 4).Span);
            if (idx < 0) return BadRequest("Negative index");
            requestedIndices.Add(idx);
        }
        using var zip = ZipFile.OpenRead(zipPath);
        var entries = zip.Entries
            .Where(e => e.Name != "")
            .OrderBy(e => e.FullName, StringComparer.Ordinal)
            .ToArray();

        foreach (var idx in requestedIndices)
        {
            if (idx >= entries.Length)
                return BadRequest($"Index {idx} out of range (manifest has {entries.Length} entries)");
        }
        var streamHeader = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(streamHeader, 0);
        await Response.Body.WriteAsync(streamHeader, cancel);

        var fileHeader = new byte[4];
        var buf = ArrayPool<byte>.Shared.Rent(128 * 1024);
        try
        {
            foreach (var idx in requestedIndices)
            {
                cancel.ThrowIfCancellationRequested();
                var entry = entries[idx];
                using var ms = new MemoryStream((int)entry.Length + 1);
                using (var entryStream = entry.Open())
                    await entryStream.CopyToAsync(ms, cancel);

                var fileBytes = ms.GetBuffer();
                var fileLen = (int)ms.Length;

                BinaryPrimitives.WriteInt32LittleEndian(fileHeader, fileLen);
                await Response.Body.WriteAsync(fileHeader, cancel);
                await Response.Body.WriteAsync(fileBytes.AsMemory(0, fileLen), cancel);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }

        return new NoOpResult();
    }

    private string? ResolveZipPath(string version, string rid)
    {
        if (string.IsNullOrWhiteSpace(_options.FileDiskPath)) return null;

        var root = Path.GetFullPath(_options.FileDiskPath);
        var fileName = $"LunaLauncher-{version}-{rid}.zip";
        var path = Path.Combine(root, fileName);

        if (!System.IO.File.Exists(path))
        {
            logger.LogDebug("Launcher zip not found: {Path}", path);
            return null;
        }

        return path;
    }

    private sealed class NoOpResult : IActionResult
    {
        public Task ExecuteResultAsync(ActionContext context) => Task.CompletedTask;
    }
}
