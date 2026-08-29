using Arbitarr.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Arbitarr.Data;

/// <summary>
/// EF Core context for arr-searcher's persistence foundation (Step 2). Accepts its connection
/// configuration via <see cref="DbContextOptions{TContext}"/> so DI/composition (Host) controls
/// the connection string; this context intentionally does not configure WAL mode, busy_timeout,
/// or any pragma itself — that is owned by the connection-string/pragma configuration layer
/// (Step 2, worker-2's scope), not the schema.
/// </summary>
public sealed class ArbitarrDbContext : DbContext
{
    public ArbitarrDbContext(DbContextOptions<ArbitarrDbContext> options)
        : base(options)
    {
    }

    public DbSet<MetadataCacheEntry> MetadataCacheEntries => Set<MetadataCacheEntry>();

    public DbSet<SearchResultCacheEntry> SearchResultCacheEntries => Set<SearchResultCacheEntry>();

    public DbSet<QuerySnapshotCacheEntry> QuerySnapshotCacheEntries => Set<QuerySnapshotCacheEntry>();

    public DbSet<CapsCacheEntry> CapsCacheEntries => Set<CapsCacheEntry>();

    public DbSet<SourceHealthRecord> SourceHealthRecords => Set<SourceHealthRecord>();

    public DbSet<SuppressionAuditLogEntry> SuppressionAuditLogEntries => Set<SuppressionAuditLogEntry>();

    public DbSet<SettingEntry> Settings => Set<SettingEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<MetadataCacheEntry>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.SeriesKey, e.Source }).IsUnique();
            entity.Property(e => e.SeriesKey).IsRequired();
            entity.Property(e => e.Source).IsRequired();
            entity.Property(e => e.PayloadJson).IsRequired();
            entity.Property(e => e.SourceSnapshotVersion).IsRequired();
        });

        modelBuilder.Entity<SearchResultCacheEntry>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.QueryKey).IsUnique();
            entity.HasIndex(e => e.ServeUntil);
            entity.HasIndex(e => e.LastAccessedAt);
            entity.Property(e => e.QueryKey).IsRequired();
            entity.Property(e => e.PayloadJson).IsRequired();
        });

        modelBuilder.Entity<QuerySnapshotCacheEntry>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.SnapshotToken).IsUnique();
            entity.HasIndex(e => e.ExpiresAt);
            entity.Property(e => e.SnapshotToken).IsRequired();
            entity.Property(e => e.PayloadJson).IsRequired();
        });

        modelBuilder.Entity<CapsCacheEntry>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.SourceName).IsUnique();
            entity.Property(e => e.SourceName).IsRequired();
            entity.Property(e => e.PayloadJson).IsRequired();
        });

        modelBuilder.Entity<SourceHealthRecord>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.SourceName).IsUnique();
            entity.Property(e => e.SourceName).IsRequired();
        });

        modelBuilder.Entity<SuppressionAuditLogEntry>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.OccurredAt);
            entity.HasIndex(e => e.QueryKey);
            entity.Property(e => e.ReleaseIdentifier).IsRequired();
            entity.Property(e => e.QueryKey).IsRequired();
            entity.Property(e => e.RuleName).IsRequired();
            entity.Property(e => e.Reason).IsRequired();
        });

        modelBuilder.Entity<SettingEntry>(entity =>
        {
            entity.HasKey(e => e.Name);
            entity.Property(e => e.Name).IsRequired();
            entity.Property(e => e.Value).IsRequired();
        });
    }
}
