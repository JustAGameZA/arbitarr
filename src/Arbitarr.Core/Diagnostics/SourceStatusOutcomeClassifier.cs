using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace Arbitarr.Core.Diagnostics;

/// <summary>
/// Reduces an exception to a <see cref="SourceStatusOutcome"/> (arb-mhd2), the closed value the
/// unauthenticated <c>GET /api/status</c> publishes in place of the free-text description
/// <see cref="SanitizedErrorDescription"/> produces.
///
/// <para><b>Called AT THE WRITER, beside the existing <c>LastError</c> assignment</b> — in
/// <c>SourceCircuitBreaker.RecordFailure</c> and <c>RefreshWorkerHealth.CycleFaulted</c>'s caller —
/// so the two always describe the same failure. Classifying the sanitised STRING at projection time
/// instead would make the public body depend on parsing wording built for humans, and would
/// misclassify silently the first time that wording changed. This sees the exception itself, which
/// is the only thing that actually knows what went wrong.</para>
///
/// <para><b>This never returns <see cref="SourceStatusOutcome.Unknown"/>.</b> That member exists
/// only for a persisted row the adapter cannot interpret; a live failure always classifies to a
/// real outcome, with <see cref="SourceStatusOutcome.InternalError"/> as the catch-all. Pinned by a
/// test, because the whole value of "unknown means legacy data" is lost if a writer can also mint
/// it.</para>
/// </summary>
public static class SourceStatusOutcomeClassifier
{
    /// <summary>
    /// Classifies <paramref name="ex"/>. Arm order is load-bearing where noted; see each arm.
    /// </summary>
    public static SourceStatusOutcome Classify(Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);

        return ex switch
        {
            // Must precede the general HttpRequestException arm: an auth rejection is the one
            // outcome here an operator can only fix by re-entering a credential, and it is the same
            // condition SourceBackoffState.IsPermanentlyDisabled escalates to on the backoff side.
            // OllamaRequestException derives from HttpRequestException and so is covered by these
            // status arms too, deliberately: its excerpt is a detail, not a different KIND of
            // failure.
            HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden }
                => SourceStatusOutcome.AuthRejected,

            // Upstream answered with a status, and it was not an auth rejection. "Answered at all"
            // is what separates this from Unreachable below.
            HttpRequestException { StatusCode: not null }
                => SourceStatusOutcome.UpstreamError,

            // A timeout surfaces as HttpRequestException wrapping a TimeoutException on .NET's
            // HttpClient timeout path, so this is checked before the status-less arm treats it as a
            // connection failure. Distinguishing the two is the point of having both (see
            // SourceStatusOutcome.Unreachable).
            HttpRequestException { InnerException: TimeoutException }
                => SourceStatusOutcome.Timeout,

            // No status: the response never came. DNS failure, connection refused, no route.
            HttpRequestException => SourceStatusOutcome.Unreachable,

            // A socket failure reaching us unwrapped is the same condition by a different route.
            SocketException => SourceStatusOutcome.Unreachable,

            // TaskCanceledException derives from OperationCanceledException and is what HttpClient
            // throws when ITS timeout elapses. This classifier is only ever called on a FAILURE
            // path — a caller-requested cancellation is handled before the failure handler in both
            // writers (RefreshWorker breaks out of its loop on a cancelled stoppingToken rather
            // than calling CycleFaulted), so a cancellation arriving here is a timeout.
            TaskCanceledException or TimeoutException => SourceStatusOutcome.Timeout,

            // Our own fault, not upstream's. The catch-all deliberately lands here rather than on
            // UpstreamError: sending an operator to investigate a healthy indexer for a bug on this
            // side is the more expensive wrong answer.
            _ => SourceStatusOutcome.InternalError,
        };
    }

    /// <summary>
    /// arb-mhd2: the HTTP status upstream answered with, when <paramref name="ex"/> carries one, else
    /// null. Read STRUCTURALLY off the exception, never parsed back out of a sanitised description —
    /// the same rule <see cref="Classify"/> follows, and for the same reason: a value recovered from
    /// prose would break silently the first time the wording changed.
    ///
    /// <para>Admin-gated at every sink. A bare status code is not itself topology, but it is part of
    /// the failure DETAIL that arb-mhd2 moved behind the key as one unit, and splitting it back out
    /// onto the public route would re-open a channel the closed outcome exists to close: "which 4xx"
    /// is exactly the extra bit an unauthenticated caller should not get.</para>
    /// </summary>
    public static int? UpstreamStatusCode(Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);

        // Covers OllamaRequestException too, which derives from HttpRequestException.
        return ex is HttpRequestException { StatusCode: { } status } ? (int)status : null;
    }
}
