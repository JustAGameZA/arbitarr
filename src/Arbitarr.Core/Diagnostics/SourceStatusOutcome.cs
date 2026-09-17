namespace Arbitarr.Core.Diagnostics;

/// <summary>
/// WHY a source call or a refresh cycle last failed, in a CLOSED set safe to publish on the
/// unauthenticated <c>GET /api/status</c> (arb-mhd2, arb-ji3f).
///
/// <para><b>This exists so the public route can say something useful without carrying text.</b>
/// <c>CircuitBreakerSnapshot.LastError</c> and <c>RefreshWorkerHealth.LastError</c> are free text —
/// sanitised by <see cref="SanitizedErrorDescription"/>, but still a string built partly from
/// upstream material (see that type's arb-1rr remarks on the one admitted excerpt). A C# enum
/// cannot be minted from a wire value or from upstream text, so publishing THIS instead of the
/// string makes the public body incapable of carrying a leak by construction rather than by
/// correct scrubbing. The text itself moves behind the admin key.</para>
///
/// <para><b>THIS IS DELIBERATELY NOT <c>Arbitarr.Data.Sources.SourceCallOutcome</c>, AND THE TWO
/// MUST NOT BE MERGED LATER.</b> Two independent reasons, either one sufficient:</para>
///
/// <para>1. <b>Layering, and it is mechanically enforced.</b> <c>SourceCallOutcome</c> lives in
/// <c>Arbitarr.Data</c>. The two writers that must set this value —
/// <c>SourceCircuitBreaker.RecordFailure</c> and <c>RefreshWorkerHealth.CycleFaulted</c> — both live
/// in <c>Arbitarr.Core</c>, which references no other Arbitarr project and cannot:
/// <c>Arbitarr.Architecture.Tests.CoreIsolationTests</c> asserts by reflection that
/// the built Core assembly references zero <c>Arbitarr.*</c> assemblies. Putting the Data enum on a
/// Core type turns that test red. Same constraint already recorded on
/// <see cref="SanitizedErrorDescription"/>, <c>OllamaKeepAlive</c> and <c>VerdictSchema</c>.</para>
///
/// <para>2. <b>They answer different questions, so collapsing them would lose one.</b>
/// <c>SourceCallOutcome</c> carries a BACKOFF DECISION: its four values exist because each leads to
/// a different escalation action, and <c>SourceBackoffStore.RecordOutcomeAsync</c> switches on it.
/// This enum carries a DIAGNOSTIC OUTCOME for an operator to read. The distinction is not cosmetic:
/// <see cref="Timeout"/> and <see cref="Unreachable"/> are one single <c>TransientFailure</c> over
/// there — collapsing them is precisely what this type exists to undo — while that enum's
/// <c>NotAttempted</c> has no diagnostic meaning here at all. (Those names are written as plain text
/// rather than as <c>see cref</c> links for the layering reason in point 1: Core cannot reference
/// the type they live on.) Widening <c>SourceCallOutcome</c> to serve both would
/// also add members to the enum backoff decisioning switches on, which arb-mhd2 forbids changing.</para>
///
/// <para><b>Classified AT THE WRITER, from the exception, never from the sanitised string.</b>
/// Re-deriving the outcome by pattern-matching <c>LastError</c> at projection time would make the
/// public body's safety depend on parsing a string built for humans, and would silently
/// misclassify the moment the scrubber's wording changed.</para>
///
/// <para><b>Persisted by NAME, never by numeric value</b> (<c>SourceHealthRecord.LastOutcome</c>).
/// Read back by explicit name matching only — never <c>Enum.TryParse</c>, which accepts the numeric
/// form and would let a stored "1" select a member (CLAUDE.md section 3). Renumbering these members
/// is therefore safe; RENAMING one is a data migration.</para>
/// </summary>
public enum SourceStatusOutcome
{
    /// <summary>
    /// Nothing has failed: no failure has been recorded, or the last call succeeded. The value a
    /// healthy source reports, and the one <c>RefreshWorkerHealth</c> resets to on a clean cycle.
    /// </summary>
    None = 0,

    /// <summary>
    /// Upstream answered, and its answer was an error — an HTTP status outside 2xx that is not an
    /// authentication rejection. The source is reachable and our credential is accepted; the
    /// request itself was refused or failed.
    /// </summary>
    UpstreamError = 1,

    /// <summary>
    /// Upstream could not be reached at all: DNS failure, connection refused, no route. Distinct
    /// from <see cref="Timeout"/> because the remedies differ — this points at the address or at
    /// the service being down, a timeout points at it being slow or overloaded.
    /// </summary>
    Unreachable = 2,

    /// <summary>
    /// The call was abandoned because it took too long. Kept apart from <see cref="Unreachable"/>
    /// for the reason given there; both collapse into <c>TransientFailure</c> on the backoff side,
    /// which is exactly the distinction this enum restores for the operator.
    /// </summary>
    Timeout = 3,

    /// <summary>
    /// Upstream rejected our credential (401 or 403). The one outcome here that an operator can
    /// only fix by re-entering a key, and the same condition
    /// <c>SourceBackoffState.IsPermanentlyDisabled</c> escalates to on the backoff side.
    /// </summary>
    AuthRejected = 4,

    /// <summary>
    /// The failure was Arbitarr's own, not upstream's — an unexpected exception on our side of the
    /// call. Named separately so an operator is not sent to investigate an indexer that never
    /// misbehaved.
    /// </summary>
    InternalError = 5,

    /// <summary>
    /// The stored outcome could not be interpreted: a legacy row written before this column existed
    /// that HAS a recorded error but no outcome to explain it, or a stored name no member here
    /// matches. A legacy row with no error is NOT this — never having failed reads back as
    /// <see cref="None"/>, so an upgrade does not turn every healthy source into an unexplained
    /// failure.
    ///
    /// <para><b>Unreachable from any live writer, and that is asserted by a test.</b> No
    /// classification path produces this value — it is exclusively what the persistence adapter
    /// substitutes when reading a row it cannot interpret. That is deliberate: the alternative,
    /// inferring an outcome from the legacy <c>LastError</c> TEXT, is the projection-time
    /// classification this design exists to forbid, and it would resurrect a dependency on the
    /// scrubber's wording. Reporting "unknown" honestly costs an operator one stale diagnostic on
    /// rows written before the upgrade, and nothing afterwards.</para>
    /// </summary>
    Unknown = 6,
}
