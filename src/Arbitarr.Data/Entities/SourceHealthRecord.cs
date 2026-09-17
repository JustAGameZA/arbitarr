namespace Arbitarr.Data.Entities;

/// <summary>
/// Per-source circuit breaker state. Named distinctly from
/// <c>Arbitarr.Core.Sources.SourceHealth</c> (the in-memory point-in-time snapshot type) since
/// this is the persisted breaker state row. Default curve values (consecutive failures to open,
/// backoff growth/ceiling, jitter, probe interval) come from <c>docs/step0-measurements.md</c> and
/// are seeded/configured by worker-2's runtime logic, not hardcoded into this entity's shape.
/// </summary>
public sealed class SourceHealthRecord
{
    /// <summary>Surrogate primary key.</summary>
    public long Id { get; set; }

    /// <summary>Name/identifier of the upstream source this breaker state tracks.</summary>
    public required string SourceName { get; set; }

    /// <summary>Current breaker state.</summary>
    public CircuitBreakerState State { get; set; }

    /// <summary>Consecutive failure count observed since the breaker last closed.</summary>
    public int ConsecutiveFailures { get; set; }

    /// <summary>
    /// Un-jittered doubling-curve base backoff duration in seconds (capped at a ceiling). Kept
    /// separately from <see cref="CurrentBackoffSeconds"/> so restarts resume the doubling curve
    /// from the pure base rather than re-doubling an already-jittered value (M3-12).
    /// </summary>
    public double BaseBackoffSeconds { get; set; }

    /// <summary>Current (jittered) backoff duration in seconds (doubling curve, capped at a ceiling).</summary>
    public double CurrentBackoffSeconds { get; set; }

    /// <summary>Timestamp of the most recent failure, if any.</summary>
    public DateTimeOffset? LastFailureAt { get; set; }

    /// <summary>Timestamp of the most recent success, if any.</summary>
    public DateTimeOffset? LastSuccessAt { get; set; }

    /// <summary>Most recent error message, if any.</summary>
    public string? LastError { get; set; }

    /// <summary>
    /// arb-mhd2: WHY the most recent failure happened, as the NAME of an
    /// <c>Arbitarr.Core.Diagnostics.SourceStatusOutcome</c> member — "AuthRejected", "Timeout", and
    /// so on. The companion to <see cref="LastError"/>, and what the unauthenticated
    /// <c>GET /api/status</c> publishes in its place.
    ///
    /// <para><b>Stored as the NAME, not the numeric value</b>, following
    /// <c>SourceBackoffState.LastOutcome</c>'s precedent. A name survives the enum being renumbered
    /// and is legible in the database; the cost — that RENAMING a member becomes a data migration —
    /// is the cheaper of the two, since renumbering is the thing a routine edit does by accident.</para>
    ///
    /// <para><b>Read back by EXPLICIT NAME MATCHING, never <c>Enum.TryParse</c></b> (CLAUDE.md
    /// section 3): that helper also accepts the NUMERIC form, so a stored "4" would select
    /// <c>AuthRejected</c> through a shape no writer here produces. The matching lives in
    /// <c>SourceHealthRepository.ToOutcome</c>.</para>
    ///
    /// <para><b>Null means a legacy row</b> — written before this column existed. Alone that says
    /// nothing about health, since EVERY row has a null here immediately after the upgrade, so the
    /// read splits on whether <see cref="LastError"/> is set: a null error projects as <c>None</c>
    /// (the source has simply never failed), a non-null error projects as <c>Unknown</c> (it failed,
    /// and the reason was not recorded). That split reads whether the error is PRESENT and never
    /// what it SAYS — an outcome inferred from the error TEXT would reintroduce exactly the
    /// projection-time classification this design forbids; see the enum member's own remarks.</para>
    /// </summary>
    public string? LastOutcome { get; set; }

    /// <summary>
    /// arb-mhd2: the HTTP status upstream answered the most recent failure with, or null when it
    /// carried none (a connection failure, a timeout, an internal fault) or when the row predates
    /// this column. Persisted beside <see cref="LastOutcome"/> so the admin diagnostics route can
    /// report it after a restart, exactly as it reports <see cref="LastError"/>.
    ///
    /// <para>An <c>int?</c> rather than a string: it is a number, and storing it as one means no
    /// parsing step exists anywhere that could accept something that is not a status code. Null is a
    /// distinct state (no status), so this deliberately carries no default — see
    /// <c>docs/standards/data.md</c> on nullable columns that mean a state.</para>
    /// </summary>
    public int? LastUpstreamStatusCode { get; set; }

    /// <summary>When the breaker may next attempt a probe request while open/half-open.</summary>
    public DateTimeOffset? NextProbeAt { get; set; }
}

/// <summary>Circuit breaker lifecycle state for a source health record.</summary>
public enum CircuitBreakerState
{
    Closed = 0,
    Open = 1,
    HalfOpen = 2,
}
