namespace Arbitarr.Core.Security;

/// <summary>
/// #58: records that a key just authenticated a request, so an operator can answer "is anything
/// still using this?" before revoking it. Without that answer revocation is guesswork and operators
/// will not do it, which makes the whole feature ornamental.
///
/// <para>Declared in Core so the gate (Arbitarr.Api) can call it without referencing Arbitarr.Data —
/// the same isolation <c>IEventSink</c> observes, and required by <c>CoreIsolationTests</c>.</para>
///
/// <para><b>THIS MUST NOT BE A SYNCHRONOUS WRITE.</b> It is on the hot path of every gated request,
/// and a naive implementation turns each one into an extra SQLite UPDATE — on the single-writer
/// database that already serializes the request's real work. The implementation
/// (<c>ThrottledApiKeyLastUsedRecorder</c>) therefore coalesces: at most one write per key per
/// <see cref="ThrottleWindow"/>, and never inline with the request. The return type is
/// <c>void</c> rather than a Task deliberately — there is nothing for a caller to await, and giving
/// them one would invite exactly the inline write this exists to prevent.</para>
/// </summary>
public interface IApiKeyLastUsedRecorder
{
    /// <summary>
    /// How stale a recorded last-used timestamp is allowed to be. One minute: fine enough that
    /// "used in the last few minutes" is a truthful answer to the question an operator is asking
    /// before revoking, and coarse enough that a *arr instance polling every few seconds produces
    /// one write a minute rather than one per request.
    /// </summary>
    public static readonly TimeSpan ThrottleWindow = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Notes that key <paramref name="keyId"/> was used. Returns immediately; the write, if this
    /// call earns one, happens off the request path.
    /// </summary>
    void RecordUsed(long keyId);
}
