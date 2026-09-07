using Arbitarr.Core.Filtering;
using Arbitarr.Core.Security;
using Arbitarr.Data.Entities;
using Arbitarr.Data.Security;
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

    /// <summary>
    /// #58: named, scoped admin API keys. Distinct from <see cref="ApiKeyProfiles"/>, which maps a
    /// Torznab/Newznab client apikey to a filter profile — a different credential for a different
    /// surface. No row here holds a key value; see <see cref="ApiKeyEntry"/>.
    /// </summary>
    public DbSet<ApiKeyEntry> ApiKeys => Set<ApiKeyEntry>();

    /// <summary>
    /// #44: human operator accounts. Distinct from <see cref="ApiKeys"/>, which authenticates
    /// MACHINE callers — two credential kinds for two kinds of caller, resolving to the one shared
    /// <see cref="ApiKeyScope"/> vocabulary rather than to two authorization models (the owner
    /// ruling on #44/#58). No row here holds a password; see <see cref="UserEntry"/>.
    /// </summary>
    public DbSet<UserEntry> Users => Set<UserEntry>();

    /// <summary>
    /// #44: live and revoked server-side sessions, so logout and expiry are real rather than a
    /// discarded cookie. No row here holds a token value; see <see cref="SessionEntry"/>.
    /// </summary>
    public DbSet<SessionEntry> Sessions => Set<SessionEntry>();

    public DbSet<VerdictCacheEntry> VerdictCacheEntries => Set<VerdictCacheEntry>();

    /// <summary>
    /// The shared event store (#55 step 1 / #54's decision store — plan §2). Nothing writes to or
    /// reads from this set outside of <see cref="Events.EventRepository"/> and its tests yet.
    /// </summary>
    public DbSet<EventEntry> Events => Set<EventEntry>();

    /// <summary>
    /// #53 stage 53a: configured upstream sources. Nothing reads this set yet — env vars remain
    /// authoritative until 53b adds the DB-first, env-var-fallback resolution path (plan §3.2).
    /// </summary>
    public DbSet<Source> Sources => Set<Source>();

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

        modelBuilder.Entity<ApiKeyEntry>(entity =>
        {
            entity.HasKey(e => e.Id);
            // Unique on both: the hash because it is the verification lookup and two rows answering
            // the same presented value would make "which key was this?" unanswerable — the exact
            // attribution question #58 exists to answer; the label because two keys the operator
            // cannot tell apart defeat the same purpose from the other end.
            entity.HasIndex(e => e.KeyHash).IsUnique();
            entity.HasIndex(e => e.Label).IsUnique();
            entity.Property(e => e.Label).IsRequired().HasMaxLength(128);
            entity.Property(e => e.KeyHash).IsRequired().HasMaxLength(ApiKeyHasher.HashLength);
        });

        modelBuilder.Entity<UserEntry>(entity =>
        {
            entity.HasKey(e => e.Id);
            // UNIQUE IS LOAD-BEARING, NOT HYGIENE. This index is the atomicity mechanism for
            // first-account creation (AC4): UserRepository.CreateFirstUserAsync inserts and lets
            // the database reject a racing second insert, rather than checking "are there zero
            // users?" and then writing, which has a window between the two halves that two
            // concurrent LAN clients can both pass. A test drives that race directly. Removing or
            // relaxing this index does not merely permit duplicate names — it reopens the
            // first-account hijack the issue names as the thing to prevent.
            entity.HasIndex(e => e.Username).IsUnique();
            entity.Property(e => e.Username).IsRequired().HasMaxLength(UserRepository.MaxUsernameLength);
            // Bounded well above any KDF output this can hold (ASP.NET Core's PasswordHasher v3
            // format is 84 base64 chars) so a future cost or algorithm change has headroom.
            entity.Property(e => e.PasswordHash).IsRequired().HasMaxLength(512);
        });

        modelBuilder.Entity<SessionEntry>(entity =>
        {
            entity.HasKey(e => e.Id);
            // Unique on the hash for the same reason ApiKeyEntry.KeyHash is: it is the verification
            // lookup, and two rows answering one presented token would make "whose session is
            // this?" unanswerable.
            entity.HasIndex(e => e.TokenHash).IsUnique();
            // Sessions are looked up by user when a logout revokes every session an account holds,
            // and pruned by expiry.
            entity.HasIndex(e => e.UserId);
            entity.HasIndex(e => e.AbsoluteExpiresAt);
            entity.Property(e => e.TokenHash).IsRequired().HasMaxLength(SessionToken.HashLength);
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
            // M5 R17: rewritten title cached alongside the verdict. The bound is advisory on SQLite; it is
            // enforced in code by VerdictCacheLimits (producer + writer), which this must match.
            entity.Property(e => e.RewrittenTitle).HasMaxLength(VerdictCacheLimits.MaxRewrittenTitleLength);
        });

        modelBuilder.Entity<EventEntry>(entity =>
        {
            entity.HasKey(e => e.Id);
            // Both consumers' expected access patterns: #55 pages recent events (optionally
            // filtered by kind), #54 filters decisions specifically. A composite (Kind, OccurredAt)
            // index serves both without needing two separate indexes.
            entity.HasIndex(e => new { e.Kind, e.OccurredAt });
            entity.Property(e => e.Summary).IsRequired().HasMaxLength(1024);
            entity.Property(e => e.Reason).HasMaxLength(1024);
            entity.Property(e => e.SourceDisplayName).HasMaxLength(256);
            // #54: the review verdict hangs on this same row as nullable columns rather than in a
            // second table (plan §3.1). The note is bounded like Reason above; the bound is enforced
            // in code by EventRepository.ReviewAsync, which REJECTS an over-long note rather than
            // truncating it, on AC24's reject-never-clamp footing — silently storing a shortened
            // version of an operator's own words is a worse answer than refusing them.
            entity.Property(e => e.ReviewNote).HasMaxLength(1024);
        });

        modelBuilder.Entity<Source>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.DisplayName).IsUnique();
            entity.Property(e => e.Kind).IsRequired().HasMaxLength(64);
            entity.Property(e => e.DisplayName).IsRequired().HasMaxLength(256);
            // No HasMaxLength on BaseUrl: a URL has no natural length ceiling worth guessing at, and
            // SourceRepository.ValidateBaseUrl already rejects anything that isn't a well-formed
            // absolute http(s) URL before it reaches this table.
            entity.Property(e => e.BaseUrl).IsRequired();
        });
    }
}
