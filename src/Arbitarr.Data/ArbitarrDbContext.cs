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

    public DbSet<FilterProfileEntry> FilterProfiles => Set<FilterProfileEntry>();

    public DbSet<FilterRuleEntry> FilterRules => Set<FilterRuleEntry>();

    public DbSet<ApiKeyProfileEntry> ApiKeyProfiles => Set<ApiKeyProfileEntry>();

    public DbSet<VerdictCacheEntry> VerdictCacheEntries => Set<VerdictCacheEntry>();

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
            entity.HasIndex(e => e.LastRequestedAt);
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
            // M4 review finding (LOW): bound QueryKey/Reason length at the schema level, matching
            // FilterRuleEntry.Pattern's HasMaxLength(1024) precedent — QueryKey mirrors the raw
            // search query (bounded generously above any realistic query), Reason is a generated
            // sentence that itself now clamps the reflected query text (see
            // Arbitarr.Api.Search.FilterStage), so 1024 is comfortable headroom for it. Writers
            // truncate rather than throw (SuppressionAuditLogMapper), so an over-length value is
            // never surfaced as a runtime failure.
            entity.Property(e => e.QueryKey).IsRequired().HasMaxLength(512);
            entity.Property(e => e.RuleName).IsRequired();
            entity.Property(e => e.Reason).IsRequired().HasMaxLength(1024);
        });

        modelBuilder.Entity<SettingEntry>(entity =>
        {
            entity.HasKey(e => e.Name);
            entity.Property(e => e.Name).IsRequired();
            entity.Property(e => e.Value).IsRequired();
        });

        modelBuilder.Entity<FilterProfileEntry>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.Name).IsUnique();
            entity.Property(e => e.Name).IsRequired();
        });

        modelBuilder.Entity<FilterRuleEntry>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.FilterProfileId);
            entity.Property(e => e.Name).IsRequired();
            // M4 review finding (MEDIUM): bound pattern length at the schema level too, matching
            // Core.Settings.SettingsValidator.FilterRulePatternMaxLength (defense in depth — Core's
            // RuleImporter already rejects an over-length pattern before it ever reaches this layer).
            entity.Property(e => e.Pattern).IsRequired().HasMaxLength(1024);
        });

        modelBuilder.Entity<ApiKeyProfileEntry>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.ApiKeyName).IsUnique();
            entity.Property(e => e.ApiKeyName).IsRequired();
        });

        modelBuilder.Entity<VerdictCacheEntry>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.ReleaseKeyHash).IsUnique();
            entity.HasIndex(e => e.LastAccessedAt);
            // M5 security review (LOW): bound these at the schema level too, matching the
            // FilterRuleEntry.Pattern/SuppressionAuditLogEntry precedent — ReleaseKeyHash is a
            // fixed-length SHA-256 hex digest (64 chars), ModelName/ModelDigest/PromptVersion are
            // short identity strings with generous headroom above any realistic value.
            entity.Property(e => e.ReleaseKeyHash).IsRequired().HasMaxLength(64);
            entity.Property(e => e.ModelName).IsRequired().HasMaxLength(256);
            entity.Property(e => e.ModelDigest).IsRequired().HasMaxLength(256);
            entity.Property(e => e.PromptVersion).IsRequired().HasMaxLength(256);
        });
    }
}
