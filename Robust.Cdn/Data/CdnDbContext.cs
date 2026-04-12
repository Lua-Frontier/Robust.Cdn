using Microsoft.EntityFrameworkCore;

namespace Robust.Cdn;

public sealed class CdnDbContext(DbContextOptions<CdnDbContext> options) : DbContext(options)
{
    public DbSet<Content> Contents => Set<Content>();
    public DbSet<ContentVersion> ContentVersions => Set<ContentVersion>();
    public DbSet<ContentManifestEntry> ContentManifestEntries => Set<ContentManifestEntry>();
    public DbSet<RequestLogBlob> RequestLogBlobs => Set<RequestLogBlob>();
    public DbSet<RequestLog> RequestLogs => Set<RequestLog>();
    public DbSet<CdnFork> Forks => Set<CdnFork>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        builder.Entity<Content>(entity =>
        {
            entity.ToTable("Content", table => table.HasCheckConstraint("CK_Content_UncompressedSameSize", "\"Compression\" != 0 OR octet_length(\"Data\") = \"Size\""));
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Hash).IsRequired();
            entity.HasIndex(e => e.Hash).IsUnique();
            entity.Property(e => e.Size).IsRequired();
            entity.Property(e => e.Compression).IsRequired();
            entity.Property(e => e.Data).IsRequired();
        });

        builder.Entity<CdnFork>(entity =>
        {
            entity.ToTable("Fork");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired();
            entity.HasIndex(e => e.Name).IsUnique();
        });

        builder.Entity<ContentVersion>(entity =>
        {
            entity.ToTable("ContentVersion");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Version).IsRequired();
            entity.Property(e => e.TimeAdded).IsRequired();
            entity.Property(e => e.ManifestHash).IsRequired();
            entity.Property(e => e.ManifestData).IsRequired();
            entity.Property(e => e.CountDistinctBlobs).IsRequired();
            entity.HasIndex(e => new { e.ForkId, e.Version }).IsUnique();
            entity.HasOne(e => e.Fork).WithMany(f => f.ContentVersions).HasForeignKey(e => e.ForkId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<ContentManifestEntry>(entity =>
        {
            entity.ToTable("ContentManifestEntry");
            entity.HasKey(e => new { e.VersionId, e.ManifestIdx });
            entity.HasOne(e => e.ContentVersion).WithMany(v => v.ManifestEntries).HasForeignKey(e => e.VersionId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.Content).WithMany().HasForeignKey(e => e.ContentId).OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(e => e.ContentId).HasDatabaseName("ContentManifestEntryContentId");
        });

        builder.Entity<RequestLogBlob>(entity =>
        {
            entity.ToTable("RequestLogBlob");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Hash).IsRequired();
            entity.HasIndex(e => e.Hash).IsUnique();
            entity.Property(e => e.Data).IsRequired();
        });

        builder.Entity<RequestLog>(entity =>
        {
            entity.ToTable("RequestLog");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Time).IsRequired();
            entity.Property(e => e.Compression).IsRequired();
            entity.Property(e => e.Protocol).IsRequired();
            entity.Property(e => e.BytesSent).IsRequired();
            entity.HasOne(e => e.ContentVersion).WithMany().HasForeignKey(e => e.VersionId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.Blob).WithMany().HasForeignKey(e => e.BlobId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}

public sealed class Content
{
    public int Id { get; set; }
    public byte[] Hash { get; set; } = null!;
    public int Size { get; set; }
    public int Compression { get; set; }
    public byte[] Data { get; set; } = null!;
}

public sealed class CdnFork
{
    public int Id { get; set; }
    public string Name { get; set; } = null!;
    public ICollection<ContentVersion> ContentVersions { get; set; } = new List<ContentVersion>();
}

public sealed class ContentVersion
{
    public int Id { get; set; }
    public int ForkId { get; set; }
    public CdnFork Fork { get; set; } = null!;
    public string Version { get; set; } = null!;
    public DateTime TimeAdded { get; set; }
    public byte[] ManifestHash { get; set; } = null!;
    public byte[] ManifestData { get; set; } = null!;
    public int CountDistinctBlobs { get; set; }
    public ICollection<ContentManifestEntry> ManifestEntries { get; set; } = new List<ContentManifestEntry>();
}

public sealed class ContentManifestEntry
{
    public int VersionId { get; set; }
    public int ManifestIdx { get; set; }
    public int ContentId { get; set; }

    public ContentVersion ContentVersion { get; set; } = null!;
    public Content Content { get; set; } = null!;
}

public sealed class RequestLogBlob
{
    public int Id { get; set; }
    public byte[] Hash { get; set; } = null!;
    public byte[] Data { get; set; } = null!;
}

public sealed class RequestLog
{
    public int Id { get; set; }
    public DateTime Time { get; set; }
    public int Compression { get; set; }
    public int Protocol { get; set; }
    public int BytesSent { get; set; }
    public int VersionId { get; set; }
    public int BlobId { get; set; }

    public ContentVersion ContentVersion { get; set; } = null!;
    public RequestLogBlob Blob { get; set; } = null!;
}
