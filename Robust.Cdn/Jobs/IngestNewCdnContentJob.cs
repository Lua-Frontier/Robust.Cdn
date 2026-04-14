using System.Buffers;
using System.IO.Compression;
using System.Text;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Quartz;
using Robust.Cdn.Config;
using Robust.Cdn.Helpers;
using Robust.Cdn.Lib;
using SpaceWizards.Sodium;
using SQLitePCL;

namespace Robust.Cdn.Jobs;

[DisallowConcurrentExecution]
public sealed class IngestNewCdnContentJob(
    Database cdnDatabase,
    IOptions<CdnOptions> cdnOptions,
    IOptions<ManifestOptions> manifestOptions,
    IOptions<RobustOptions> robustOptions,
    ISchedulerFactory schedulerFactory,
    BuildDirectoryManager buildDirectoryManager,
    ILogger<IngestNewCdnContentJob> logger) : IJob
{
    public static readonly JobKey Key = new(nameof(IngestNewCdnContentJob));
    public const string KeyForkName = "ForkName";

    public static JobDataMap Data(string fork) => new()
    {
        { KeyForkName, fork }
    };

    public async Task Execute(IJobExecutionContext context)
    {
        var fork = context.MergedJobDataMap.GetString(KeyForkName) ?? throw new InvalidDataException();

        logger.LogInformation("Ingesting new versions for fork: {Fork}", fork);

        var isRobust = fork == UpdateRobustManifestJob.ForkName;
        var clientZipName = isRobust ? robustOptions.Value.ClientZipName : manifestOptions.Value.Forks[fork].ClientZipName;
        var forceMakeAvailable = !isRobust && manifestOptions.Value.Forks[fork].ForceMakeAvailableForExistingContentVersions;

        var connection = cdnDatabase.Connection;

        var (versionsToIngest, versionsToMakeAvailable) = FindNewVersions(fork, connection, clientZipName, forceMakeAvailable, isRobust);
        if (versionsToIngest.Count == 0 && versionsToMakeAvailable.Count == 0)
            return;

        if (versionsToIngest.Count > 0)
        {
            var transaction = connection.BeginTransaction();
            try
            {
                IngestNewVersions(
                    fork,
                    connection,
                    versionsToIngest,
                    ref transaction,
                    clientZipName,
                    isRobust,
                    context.CancellationToken);

                logger.LogDebug("Committing database");
                transaction.Commit();
            }
            finally
            {
                transaction.Dispose();
            }
        }

        if (versionsToMakeAvailable.Count > 0)
            await QueueManifestAvailable(fork, versionsToMakeAvailable);
    }

    private async Task QueueManifestAvailable(string fork, IEnumerable<string> newVersions)
    {
        var scheduler = await schedulerFactory.GetScheduler();
        await scheduler.TriggerJob(
            MakeNewManifestVersionsAvailableJob.Key,
            MakeNewManifestVersionsAvailableJob.Data(fork, newVersions));
    }

    private void IngestNewVersions(
        string fork,
        SqliteConnection connection,
        List<string> newVersions,
        ref SqliteTransaction transaction,
        string clientZipName,
        bool isRobust,
        CancellationToken cancel)
    {
        var cdnOpts = cdnOptions.Value;

        var forkId = EnsureForkCreated(fork, connection);

        using var stmtLookupContent = connection.Handle!.Prepare("SELECT Id FROM Content WHERE Hash = ?");
        using var stmtInsertContent = connection.Handle!.Prepare(
            "INSERT INTO Content (Hash, Size, Compression, Data) " +
            "VALUES (@Hash, @Size, @Compression, @Data) " +
            "RETURNING Id");

        using var stmtInsertContentManifestEntry = connection.Handle!.Prepare(
            "INSERT INTO ContentManifestEntry (VersionId, ManifestIdx, ContentId) " +
            "VALUES (@VersionId, @ManifestIdx, @ContentId) ");

        var hash = new byte[32];

        var readBuffer = ArrayPool<byte>.Shared.Rent(1024);
        var compressBuffer = ArrayPool<byte>.Shared.Rent(1024);

        using var compressor = new ZStdCompressionContext();
        SqliteBlobStream? blob = null;

        try
        {
            var versionIdx = 0;
            foreach (var version in newVersions)
            {
                if (versionIdx % 5 == 0)
                {
                    logger.LogDebug("Doing interim commit");

                    blob?.Dispose();
                    blob = null;

                    transaction.Commit();
                    transaction = connection.BeginTransaction();
                }

                cancel.ThrowIfCancellationRequested();

                logger.LogInformation("Ingesting new version: {Version}", version);

                var versionId = connection.ExecuteScalar<long>(
                    "INSERT INTO ContentVersion (ForkId, Version, TimeAdded, ManifestHash, ManifestData, CountDistinctBlobs) " +
                    "VALUES (@ForkId, @Version, datetime('now'), zeroblob(0), zeroblob(0), 0) " +
                    "RETURNING Id",
                    new { Version = version, ForkId = forkId });

                stmtInsertContentManifestEntry.BindInt64(1, versionId);
                var versionDir = isRobust ? buildDirectoryManager.GetRobustBuildVersionPath(version) : buildDirectoryManager.GetBuildVersionPath(fork, version);
                var exactZip = Path.Combine(versionDir, clientZipName + ".zip"); //!!!!!!!!!
                string zipFilePath;
                if (File.Exists(exactZip))
                { zipFilePath = exactZip; }
                else
                {
                    var candidates = Directory.Exists(versionDir) ? Directory.EnumerateFiles(versionDir, clientZipName + "_*.zip").ToList() : [];
                    zipFilePath = candidates
                        .OrderBy(p => p.EndsWith("_win-x64.zip", StringComparison.Ordinal) ? 0 : 1)
                        .ThenBy(p => p, StringComparer.Ordinal)
                        .FirstOrDefault()
                        ?? throw new FileNotFoundException(
                            $"Could not find client zip for '{clientZipName}' in '{versionDir}'. " +
                            $"Expected '{clientZipName}.zip' or '{clientZipName}_*.zip'.");
                }

                using var zipFile = ZipFile.OpenRead(zipFilePath);

                // TODO: hash incrementally without buffering in-memory
                var manifestStream = new MemoryStream();
                var manifestWriter = new StreamWriter(manifestStream, new UTF8Encoding(false));
                manifestWriter.Write("Robust Content Manifest 1\n");

                var newBlobCount = 0;

                var idx = 0;
                foreach (var entry in zipFile.Entries.OrderBy(e => e.FullName, StringComparer.Ordinal))
                {
                    cancel.ThrowIfCancellationRequested();

                    // Ignore directory entries.
                    if (entry.Name == "")
                        continue;

                    var dataLength = (int)entry.Length;

                    BufferHelpers.EnsurePooledBuffer(ref readBuffer, ArrayPool<byte>.Shared, dataLength);

                    var readData = readBuffer.AsSpan(0, dataLength);
                    using (var stream = entry.Open())
                    {
                        stream.ReadExact(readData);
                    }

                    // Hash the data.
                    CryptoGenericHashBlake2B.Hash(hash, readData, ReadOnlySpan<byte>.Empty);

                    // Look up if we already have this blob.
                    stmtLookupContent.BindBlob(1, hash);

                    long contentId;
                    if (stmtLookupContent.Step() == raw.SQLITE_DONE)
                    {
                        stmtLookupContent.Reset();

                        // Don't have this blob yet, add a new one!
                        newBlobCount += 1;

                        ReadOnlySpan<byte> writeData;
                        var compression = ContentCompression.None;

                        // Try compression maybe.
                        if (cdnOpts.BlobCompress)
                        {
                            BufferHelpers.EnsurePooledBuffer(
                                ref compressBuffer,
                                ArrayPool<byte>.Shared,
                                ZStd.CompressBound(dataLength));

                            var compressedLength = compressor.Compress(
                                compressBuffer,
                                readData,
                                cdnOpts.BlobCompressLevel);

                            if (compressedLength + cdnOpts.BlobCompressSavingsThreshold < dataLength)
                            {
                                compression = ContentCompression.ZStd;
                                writeData = compressBuffer.AsSpan(0, compressedLength);
                            }
                            else
                            {
                                writeData = readData;
                            }
                        }
                        else
                        {
                            writeData = readData;
                        }

                        // Insert blob database.

                        stmtInsertContent.BindBlob(1, hash); // @Hash
                        stmtInsertContent.BindInt(2, dataLength); // @Size
                        stmtInsertContent.BindInt(3, (int)compression); // @Compression
                        stmtInsertContent.BindZeroBlob(4, writeData.Length); // @Data

                        stmtInsertContent.Step();

                        contentId = stmtInsertContent.ColumnInt64(0);

                        stmtInsertContent.Reset();

                        if (blob == null)
                        {
                            blob = SqliteBlobStream.Open(
                                connection.Handle!,
                                "main",
                                "Content",
                                "Data",
                                contentId,
                                true);
                        }
                        else
                        {
                            blob.Reopen(contentId);
                        }

                        blob.Write(writeData);
                    }
                    else
                    {
                        contentId = stmtLookupContent.ColumnInt64(0);

                        stmtLookupContent.Reset();
                    }

                    // Insert into ContentManifestEntry
                    stmtInsertContentManifestEntry.BindInt64(2, idx); // @ManifestIdx
                    stmtInsertContentManifestEntry.BindInt64(3, contentId); // @ContentId

                    stmtInsertContentManifestEntry.Step();
                    stmtInsertContentManifestEntry.Reset();

                    // Write manifest entry.
                    manifestWriter.Write($"{Convert.ToHexString(hash)} {entry.FullName}\n");

                    idx += 1;
                }

                logger.LogDebug("Ingested {NewBlobCount} new blobs", newBlobCount);

                // Handle manifest hashing and compression.
                {
                    manifestWriter.Flush();
                    manifestStream.Position = 0;

                    var manifestData = manifestStream.GetBuffer().AsSpan(0, (int)manifestStream.Length);

                    var manifestHash = CryptoGenericHashBlake2B.Hash(32, manifestData, ReadOnlySpan<byte>.Empty);

                    logger.LogDebug("New manifest hash: {ManifestHash}", Convert.ToHexString(manifestHash));

                    BufferHelpers.EnsurePooledBuffer(
                        ref compressBuffer,
                        ArrayPool<byte>.Shared,
                        ZStd.CompressBound(manifestData.Length));

                    var compressedLength = compressor.Compress(
                        compressBuffer,
                        manifestData,
                        cdnOpts.ManifestCompressLevel);

                    var compressedData = compressBuffer.AsSpan(0, compressedLength);

                    connection.Execute(
                        "UPDATE ContentVersion " +
                        "SET ManifestHash = @ManifestHash, ManifestData = zeroblob(@ManifestDataSize) " +
                        "WHERE Id = @VersionId",
                        new
                        {
                            VersionId = versionId,
                            ManifestHash = manifestHash,
                            ManifestDataSize = compressedLength
                        });

                    using var manifestBlob = SqliteBlobStream.Open(
                        connection.Handle!,
                        "main",
                        "ContentVersion",
                        "ManifestData",
                        versionId,
                        true);

                    manifestBlob.Write(compressedData);
                }

                // Calculate CountBlobsDeduplicated on ContentVersion

                connection.Execute(
                    "UPDATE ContentVersion AS cv " +
                    "SET CountDistinctBlobs = " +
                    "   (SELECT COUNT(DISTINCT cme.ContentId) FROM ContentManifestEntry cme WHERE cme.VersionId = cv.Id) " +
                    "WHERE cv.Id = @VersionId",
                    new { VersionId = versionId }
                );

                versionIdx += 1;
            }
        }
        finally
        {
            blob?.Dispose();

            ArrayPool<byte>.Shared.Return(readBuffer);
            ArrayPool<byte>.Shared.Return(compressBuffer);
        }
    }

    private (List<string> versionsToIngest, List<string> versionsToMakeAvailable) FindNewVersions(
        string fork,
        SqliteConnection con,
        string clientZipName,
        bool forceMakeAvailableForExisting,
        bool isRobust)
    {
        using var stmtCheckVersion = con.Handle!.Prepare("SELECT 1 FROM ContentVersion WHERE Version = ?");

        var versionsToIngest = new List<(string, DateTime)>();
        var versionsToMakeAvailable = new List<(string, DateTime)>();

        var dir = isRobust ? buildDirectoryManager.GetRobustPath() : buildDirectoryManager.GetForkPath(fork);
        if (!Directory.Exists(dir))
            return ([], []);

        foreach (var versionDirectory in Directory.EnumerateDirectories(dir))
        {
            var createdTime = Directory.GetLastWriteTime(versionDirectory);
            var version = Path.GetFileName(versionDirectory);
            if (version.StartsWith(".", StringComparison.Ordinal))
                continue;

            logger.LogTrace("Found version directory: {VersionDir}, write time: {WriteTime}", versionDirectory,
                createdTime);

            var hasAnyClientZip =
                File.Exists(Path.Combine(versionDirectory, clientZipName + ".zip")) ||
                Directory.EnumerateFiles(versionDirectory, clientZipName + "_*.zip").Any();

            if (!hasAnyClientZip)
            {
                logger.LogWarning(
                    "On-disk version is missing client zip: {Version} (expected {Exact} or {Prefix}_*.zip)",
                    version,
                    clientZipName + ".zip",
                    clientZipName);
                continue;
            }

            stmtCheckVersion.Reset();
            stmtCheckVersion.BindString(1, version);
            if (stmtCheckVersion.Step() == raw.SQLITE_ROW)
            {
                if (forceMakeAvailableForExisting)
                {
                    versionsToMakeAvailable.Add((version, createdTime));
                    logger.LogTrace("Content DB already has version (will make available): {Version}", version);
                }
                else
                {
                    logger.LogTrace("Content DB already has version (skipping): {Version}", version);
                }
            }
            else
            {
                versionsToIngest.Add((version, createdTime));
                versionsToMakeAvailable.Add((version, createdTime));
                logger.LogTrace("Found new version to ingest: {Version}", version);
            }
        }

        return (
            versionsToIngest.OrderByDescending(x => x.Item2).Select(x => x.Item1).ToList(),
            versionsToMakeAvailable.OrderByDescending(x => x.Item2).Select(x => x.Item1).ToList()
        );
    }

    private static int EnsureForkCreated(string fork, SqliteConnection connection)
    {
        var id = connection.QuerySingleOrDefault<int?>(
            "SELECT Id FROM Fork WHERE Name = @Name",
            new { Name = fork });

        id ??= connection.QuerySingle<int>(
            "INSERT INTO Fork (Name) VALUES (@Name) RETURNING Id",
            new { Name = fork });

        return id.Value;
    }
}
