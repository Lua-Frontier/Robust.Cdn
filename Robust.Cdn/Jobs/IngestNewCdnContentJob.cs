using System.Buffers;
using System.IO.Compression;
using System.Text;
using Microsoft.Extensions.Options;
using Quartz;
using Robust.Cdn.Config;
using Robust.Cdn.Helpers;
using Robust.Cdn.Lib;
using SpaceWizards.Sodium;

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

        var (versionsToIngest, versionsToMakeAvailable) = FindNewVersions(fork, clientZipName, forceMakeAvailable, isRobust);
        if (versionsToIngest.Count == 0 && versionsToMakeAvailable.Count == 0)
            return;

        if (versionsToIngest.Count > 0)
            await IngestNewVersions(fork, versionsToIngest, clientZipName, isRobust, context.CancellationToken);

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

    private async Task IngestNewVersions(
        string fork,
        List<string> newVersions,
        string clientZipName,
        bool isRobust,
        CancellationToken cancel)
    {
        var cdnOpts = cdnOptions.Value;

        var forkId = EnsureForkCreated(fork);

        var hash = new byte[32];

        var readBuffer = ArrayPool<byte>.Shared.Rent(1024);
        var compressBuffer = ArrayPool<byte>.Shared.Rent(1024);

        using var compressor = new ZStdCompressionContext();

        try
        {
            foreach (var version in newVersions)
            {
                cancel.ThrowIfCancellationRequested();

                logger.LogInformation("Ingesting new version: {Version}", version);

                await using var tx = await cdnDatabase.Context.Database.BeginTransactionAsync(cancel);

                var contentVersion = new ContentVersion
                {
                    ForkId = forkId,
                    Version = version,
                    TimeAdded = DateTime.UtcNow,
                    ManifestHash = [],
                    ManifestData = [],
                    CountDistinctBlobs = 0
                };
                cdnDatabase.Context.ContentVersions.Add(contentVersion);
                await cdnDatabase.Context.SaveChangesAsync(cancel);
                var versionId = contentVersion.Id;

                var versionDir = isRobust
                    ? buildDirectoryManager.GetRobustBuildVersionPath(version)
                    : buildDirectoryManager.GetBuildVersionPath(fork, version);

                string zipFilePath;
                var exactZip = Path.Combine(versionDir, clientZipName + ".zip");
                if (File.Exists(exactZip))
                {
                    zipFilePath = exactZip;
                }
                else
                {
                    var candidates = Directory.Exists(versionDir)
                        ? Directory.EnumerateFiles(versionDir, clientZipName + "_*.zip").ToList()
                        : [];
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
                    var hashCopy = hash.ToArray();
                    var existingContentId = cdnDatabase.Context.Contents
                        .Where(c => c.Hash == hashCopy)
                        .Select(c => (int?)c.Id)
                        .FirstOrDefault();

                    int contentId;
                    if (existingContentId == null)
                    {
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

                        // Insert blob into database.
                        var content = new Content
                        {
                            Hash = hashCopy,
                            Size = dataLength,
                            Compression = (int)compression,
                            Data = writeData.ToArray()
                        };
                        cdnDatabase.Context.Contents.Add(content);
                        await cdnDatabase.Context.SaveChangesAsync(cancel);
                        contentId = content.Id;
                    }
                    else
                    {
                        contentId = existingContentId.Value;
                    }

                    // Insert into ContentManifestEntry
                    cdnDatabase.Context.ContentManifestEntries.Add(new ContentManifestEntry
                    {
                        VersionId = versionId,
                        ManifestIdx = idx,
                        ContentId = contentId
                    });

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

                    contentVersion.ManifestHash = manifestHash;
                    contentVersion.ManifestData = compressBuffer.AsSpan(0, compressedLength).ToArray();
                }

                // Calculate CountDistinctBlobs on ContentVersion
                contentVersion.CountDistinctBlobs = cdnDatabase.Context.ContentManifestEntries
                    .Where(cme => cme.VersionId == versionId)
                    .Select(cme => cme.ContentId)
                    .Distinct()
                    .Count();

                await cdnDatabase.Context.SaveChangesAsync(cancel);

                logger.LogDebug("Committing version {Version}", version);
                await tx.CommitAsync(cancel);

                cdnDatabase.Context.ChangeTracker.Clear();
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(readBuffer);
            ArrayPool<byte>.Shared.Return(compressBuffer);
        }
    }

    private (List<string> versionsToIngest, List<string> versionsToMakeAvailable) FindNewVersions(
        string fork,
        string clientZipName,
        bool forceMakeAvailableForExisting,
        bool isRobust)
    {
        var existingVersions = cdnDatabase.Context.ContentVersions
            .Where(cv => cv.Fork.Name == fork)
            .Select(cv => cv.Version)
            .ToHashSet(StringComparer.Ordinal);

        var versionsToIngest = new List<(string, DateTime)>();
        var versionsToMakeAvailable = new List<(string, DateTime)>();

        var dir = isRobust ? buildDirectoryManager.GetRobustPath() : buildDirectoryManager.GetForkPath(fork);
        if (!Directory.Exists(dir))
            return ([], []);

        foreach (var versionDirectory in Directory.EnumerateDirectories(dir))
        {
            var createdTime = Directory.GetLastWriteTime(versionDirectory);
            var version = Path.GetFileName(versionDirectory);
            if (version.StartsWith('.'))
                continue;

            logger.LogTrace("Found version directory: {VersionDir}, write time: {WriteTime}", versionDirectory, createdTime);

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

            if (existingVersions.Contains(version))
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

    private int EnsureForkCreated(string fork)
    {
        var existing = cdnDatabase.Context.Forks
            .Where(f => f.Name == fork)
            .Select(f => (int?)f.Id)
            .FirstOrDefault();

        if (existing != null)
            return existing.Value;

        var newFork = new CdnFork { Name = fork };
        cdnDatabase.Context.Forks.Add(newFork);
        cdnDatabase.Context.SaveChanges();
        return newFork.Id;
    }
}
