using System.Buffers.Binary;
using System.Collections;
using System.Diagnostics;
using System.Linq;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Robust.Cdn.Config;
using Robust.Cdn.Helpers;
using Robust.Cdn.Lib;
using Robust.Cdn.Services;
using SharpZstd;
using SharpZstd.Interop;

namespace Robust.Cdn.Controllers;

[ApiController]
[Route("/fork/{fork}/version/{version}")]
public class DownloadController(
    Database db,
    ILogger<DownloadController> logger,
    IOptionsSnapshot<CdnOptions> options,
    DownloadRequestLogger requestLogger)
    : ControllerBase
{
    private const int MinDownloadProtocol = 1;
    private const int MaxDownloadProtocol = 1;
    private const int MaxDownloadRequestSize = 4 * 100_000;

    private readonly CdnOptions _options = options.Value;

    [HttpGet("manifest")]
    public IActionResult GetManifest(string fork, string version)
    {
        var versionEntry = db.Context.ContentVersions
            .AsNoTracking()
            .Where(cv => cv.Fork.Name == fork && cv.Version == version)
            .Select(cv => new { cv.Id, cv.ManifestHash, cv.ManifestData })
            .FirstOrDefault();

        if (versionEntry is null)
            return NotFound();

        Response.Headers["X-Manifest-Hash"] = Convert.ToHexString(versionEntry.ManifestHash);
        var data = versionEntry.ManifestData;

        if (AcceptsZStd)
        {
            Response.Headers.ContentEncoding = "zstd";

            return File(new MemoryStream(data), "text/plain; charset=utf-8");
        }

        var decompress = new ZstdDecodeStream(new MemoryStream(data), leaveOpen: false);

        return File(decompress, "text/plain; charset=utf-8");
    }

    [HttpOptions("download")]
    public IActionResult DownloadOptions(string fork, string version)
    {
        _ = fork;
        _ = version;

        Response.Headers["X-Robust-Download-Min-Protocol"] = MinDownloadProtocol.ToString();
        Response.Headers["X-Robust-Download-Max-Protocol"] = MaxDownloadProtocol.ToString();

        return NoContent();
    }

    [HttpPost("download")]
    public async Task<IActionResult> Download(string fork, string version)
    {
        if (Request.ContentType != "application/octet-stream")
            return BadRequest("Must specify application/octet-stream Content-Type");

        if (Request.Headers["X-Robust-Download-Protocol"] != "1")
            return BadRequest("Unknown X-Robust-Download-Protocol");

        var protocol = 1;

        // TODO: this request limiting logic is pretty bad.
        HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>()!.MaxRequestBodySize = MaxDownloadRequestSize;

        var versionData = await db.Context.ContentVersions
            .AsNoTracking()
            .Where(cv => cv.Fork.Name == fork && cv.Version == version)
            .Select(cv => new { cv.Id, cv.CountDistinctBlobs })
            .FirstOrDefaultAsync();

        if (versionData is null)
            return NotFound();

        var versionId = versionData.Id;
        var countDistinctBlobs = versionData.CountDistinctBlobs;

        var entriesCount = await db.Context.ContentManifestEntries
            .AsNoTracking()
            .CountAsync(e => e.VersionId == versionId);

        var buffer = new MemoryStream();
        await Request.Body.CopyToAsync(buffer);

        var buf = buffer.GetBuffer().AsMemory(0, (int)buffer.Position);

        var bits = new BitArray(entriesCount);
        var offset = 0;
        var countFilesRequested = 0;
        while (offset < buf.Length)
        {
            var index = BinaryPrimitives.ReadInt32LittleEndian(buf.Slice(offset, 4).Span);

            if (index < 0 || index >= entriesCount)
                return BadRequest("Out of bounds manifest index");

            if (bits[index])
                return BadRequest("Cannot request file twice");

            bits[index] = true;

            offset += 4;
            countFilesRequested += 1;
        }

        var outStream = Response.Body;

        var countStream = new CountWriteStream(outStream);
        outStream = countStream;

        var optStreamCompression = _options.StreamCompress;
        var optPreCompression = _options.SendPreCompressed;
        var optAutoStreamCompressRatio = _options.AutoStreamCompressRatio;

        if (optAutoStreamCompressRatio > 0)
        {
            var requestRatio = countFilesRequested / (float) countDistinctBlobs;
            logger.LogTrace("Auto stream compression ratio: {RequestRatio}", requestRatio);
            if (requestRatio > optAutoStreamCompressRatio)
            {
                optStreamCompression = true;
                optPreCompression = false;
            }
            else
            {
                optStreamCompression = false;
                optPreCompression = true;
            }
        }

        var doStreamCompression = optStreamCompression && AcceptsZStd;
        logger.LogTrace("Transfer is using stream-compression: {PreCompressed}", doStreamCompression);

        if (doStreamCompression)
        {
            var zStdCompressStream = new ZstdEncodeStream(outStream, leaveOpen: false);
            zStdCompressStream.Encoder.SetParameter(
                ZSTD_cParameter.ZSTD_c_compressionLevel,
                _options.StreamCompressLevel);

            outStream = zStdCompressStream;
            Response.Headers.ContentEncoding = "zstd";
        }

        // Compression options for individual compression get kind of gnarly here:
        // We cannot assume that the database was constructed with the current set of options
        // that is, individual compression and such.
        // If you ingest all versions with individual compression OFF then enable it,
        // we have no way to know whether the current blobs are properly compressed.
        // Also, you can have individual compression OFF now, and still have compressed blobs in the DB.
        // For this reason, we basically ignore CdnOptions.IndividualCompression here, unlike engine-side ACZ.
        // Whether pre-compression is done is actually based off IndividualDecompression instead.
        // Stream compression does not do overriding behavior it just sits on top of everything if you turn it on.

        var preCompressed = optPreCompression;

        logger.LogTrace("Transfer is using pre-compression: {PreCompressed}", preCompressed);

        var fileHeaderSize = 4;
        if (preCompressed)
            fileHeaderSize += 4;

        var fileHeader = new byte[fileHeaderSize];

        await using (outStream)
        {
            var streamHeader = new byte[4];
            DownloadStreamHeaderFlags streamHeaderFlags = 0;
            if (preCompressed)
                streamHeaderFlags |= DownloadStreamHeaderFlags.PreCompressed;

            BinaryPrimitives.WriteInt32LittleEndian(streamHeader, (int)streamHeaderFlags);

            await outStream.WriteAsync(streamHeader);

            try
            {
                offset = 0;
                var swSqlite = new Stopwatch();
                var count = 0;
                while (offset < buf.Length)
                {
                    var index = BinaryPrimitives.ReadInt32LittleEndian(buf.Slice(offset, 4).Span);

                    swSqlite.Start();

                    var content = await db.Context.ContentManifestEntries
                    .AsNoTracking()
                    .Where(cme => cme.VersionId == versionId && cme.ManifestIdx == index)
                    .Select(cme => new { Compression = (ContentCompression)cme.Content.Compression, cme.Content.Size, cme.Content.Data })
                    .FirstAsync();

                    swSqlite.Stop();

                    var compression = content.Compression;
                    var size = content.Size;
                    var data = content.Data;

                    // _aczSawmill.Debug($"{index:D5}: {blobLength:D8} {dataOffset:D8} {dataLength:D8}");

                    BinaryPrimitives.WriteInt32LittleEndian(fileHeader, size);

                    Stream copyFromStream = new MemoryStream(data);
                    ZStdDecompressStream? localDecompress = null;
                    if (preCompressed)
                    {
                        // If we are doing pre-compression, just write the DB contents directly.
                        BinaryPrimitives.WriteInt32LittleEndian(
                            fileHeader.AsSpan(4, 4),
                            compression == ContentCompression.ZStd ? data.Length : 0);
                    }
                    else if (compression == ContentCompression.ZStd)
                    {
                        // If we are not doing pre-compression but the DB entry is compressed, we have to decompress!
                        localDecompress = new ZStdDecompressStream(copyFromStream, ownStream: false);
                        copyFromStream = localDecompress;
                    }

                    await outStream.WriteAsync(fileHeader);

                    await copyFromStream.CopyToAsync(outStream);

                    localDecompress?.Dispose();

                    offset += 4;
                    count += 1;
                }

                logger.LogTrace(
                    "Total PostgreSQL: {SqliteElapsed} ms, ns / iter: {NanosPerIter}",
                    swSqlite.ElapsedMilliseconds,
                    swSqlite.Elapsed.TotalMilliseconds * 1_000_000 / count);
            }
            finally
            {
                // No blob to dispose
            }
        }

        var bytesSent = countStream.Written;
        logger.LogTrace("Total data sent: {BytesSent} B", bytesSent);

        if (_options.LogRequests)
        {
            var logCompression = DownloadRequestLogger.RequestLogCompression.None;
            if (preCompressed)
                logCompression |= DownloadRequestLogger.RequestLogCompression.PreCompress;
            if (doStreamCompression)
                logCompression |= DownloadRequestLogger.RequestLogCompression.Stream;

            var log = new DownloadRequestLogger.RequestLog(
                buf, logCompression, protocol, DateTime.UtcNow, versionId, bytesSent);

            await requestLogger.QueueLog(log);
        }

        return new NoOpActionResult();
    }

    // TODO: Crappy Accept-Encoding parser
    private bool AcceptsZStd => Request.Headers.AcceptEncoding.Count > 0
                                && Request.Headers.AcceptEncoding[0] is { } header
                                && header.Contains("zstd");

    public sealed class NoOpActionResult : IActionResult
    {
        public Task ExecuteResultAsync(ActionContext context)
        {
            return Task.CompletedTask;
        }
    }

    [Flags]
    private enum DownloadStreamHeaderFlags
    {
        None = 0,

        /// <summary>
        /// If this flag is set on the download stream, individual files have been pre-compressed by the server.
        /// This means each file has a compression header, and the launcher should not attempt to compress files itself.
        /// </summary>
        PreCompressed = 1 << 0
    }
}
