using Microsoft.EntityFrameworkCore;

namespace Robust.Cdn;

public sealed class ManifestDbContext(DbContextOptions<ManifestDbContext> options) : DbContext(options)
{
    public DbSet<ManifestFork> Forks => Set<ManifestFork>();
    public DbSet<ForkVersion> ForkVersions => Set<ForkVersion>();
    public DbSet<ForkVersionServerBuild> ForkVersionServerBuilds => Set<ForkVersionServerBuild>();
    public DbSet<PublishInProgress> PublishInProgresses => Set<PublishInProgress>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        builder.Entity<ManifestFork>(entity =>
        {
            entity.ToTable("Fork");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired();
            entity.HasIndex(e => e.Name).IsUnique();
            entity.Property(e => e.ServerManifestCache).IsRequired(false);
        });

        builder.Entity<ForkVersion>(entity =>
        {
            entity.ToTable("ForkVersion");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired();
            entity.Property(e => e.PublishedTime).IsRequired();
            entity.Property(e => e.ClientFileName).IsRequired();
            entity.Property(e => e.Sha256).IsRequired();
            entity.Property(e => e.EngineVersion).IsRequired(false);
            entity.Property(e => e.Available).HasDefaultValue(false);
            entity.HasIndex(e => new { e.ForkId, e.Name }).IsUnique();
            entity.HasOne(e => e.Fork).WithMany(f => f.Versions).HasForeignKey(e => e.ForkId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<ForkVersionServerBuild>(entity =>
        {
            entity.ToTable("ForkVersionServerBuild");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Platform).IsRequired();
            entity.Property(e => e.FileName).IsRequired();
            entity.Property(e => e.Sha256).IsRequired();
            entity.Property(e => e.FileSize).IsRequired(false);
            entity.HasOne(e => e.ForkVersion).WithMany(v => v.ServerBuilds).HasForeignKey(e => e.ForkVersionId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(e => new { e.ForkVersionId, e.Platform }).IsUnique();
            entity.HasIndex(e => new { e.ForkVersionId, e.FileName }).IsUnique();
        });

        builder.Entity<PublishInProgress>(entity =>
        {
            entity.ToTable("PublishInProgress");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Version).IsRequired();
            entity.Property(e => e.StartTime).IsRequired();
            entity.Property(e => e.EngineVersion).IsRequired(false);
            entity.HasIndex(e => new { e.ForkId, e.Version }).IsUnique();
            entity.HasOne(e => e.Fork).WithMany(f => f.InProgressPublishes).HasForeignKey(e => e.ForkId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}

public sealed class ManifestFork
{
    public int Id { get; set; }
    public string Name { get; set; } = null!;
    public byte[]? ServerManifestCache { get; set; }
    public ICollection<ForkVersion> Versions { get; set; } = new List<ForkVersion>();
    public ICollection<PublishInProgress> InProgressPublishes { get; set; } = new List<PublishInProgress>();
}

public sealed class ForkVersion
{
    public int Id { get; set; }
    public int ForkId { get; set; }
    public ManifestFork Fork { get; set; } = null!;
    public string Name { get; set; } = null!;
    public DateTime PublishedTime { get; set; }
    public string ClientFileName { get; set; } = null!;
    public byte[] Sha256 { get; set; } = null!;
    public string? EngineVersion { get; set; }
    public bool Available { get; set; }
    public ICollection<ForkVersionServerBuild> ServerBuilds { get; set; } = new List<ForkVersionServerBuild>();
}

public sealed class ForkVersionServerBuild
{
    public int Id { get; set; }
    public int ForkVersionId { get; set; }
    public ForkVersion ForkVersion { get; set; } = null!;
    public string Platform { get; set; } = null!;
    public string FileName { get; set; } = null!;
    public byte[] Sha256 { get; set; } = null!;
    public int? FileSize { get; set; }
}

public sealed class PublishInProgress
{
    public int Id { get; set; }
    public string Version { get; set; } = null!;
    public int ForkId { get; set; }
    public ManifestFork Fork { get; set; } = null!;
    public DateTime StartTime { get; set; }
    public string? EngineVersion { get; set; }
}
