using System.Net;
using System.Net.Http;

namespace Arbitarr.Core.Ai;

/// <summary>
/// arb-1rr: a non-2xx answer from Ollama, carrying a BOUNDED EXCERPT of the response body alongside
/// the status code.
///
/// <para><b>Why the body is carried at all.</b> Ollama reports why it rejected a request only in the
/// response body — <c>{"error":"time: missing unit in duration \"-1\""}</c> and its siblings. Before
/// this type, <c>EnsureSuccessStatusCode</c> threw with the body already discarded, so every one of
/// those distinct faults reached <c>SourceHealth.LastError</c> as the same
/// "HttpRequestException (400 BadRequest)" — true, and useless: an operator could not tell a
/// rejected option from an unknown model, which is how a Settings "Test" could pass while 100% of
/// real classifications failed.</para>
///
/// <para><b>Why it derives from <see cref="HttpRequestException"/>.</b> The catch sites that already
/// handle a failed Ollama call (the circuit breaker's <c>RecordFailureAsync</c>, the verdict chain's
/// fail-open path) match on <see cref="HttpRequestException"/>. Deriving keeps every one of them
/// working unchanged, so adding the excerpt cannot alter which failures are caught or how the
/// breaker trips — only what is said about them.</para>
///
/// <para><b>Why the excerpt is capped at <see cref="MaxExcerptLength"/> characters.</b> This string
/// is stored per source and served on a dashboard; an unbounded upstream body must not be able to
/// push arbitrary length into either. The cap is applied HERE, at the point of capture, rather than
/// at the point of display, so no later path can be given the untruncated value by accident.</para>
///
/// <para><b>THE EXCERPT IS SCRUBBED ONCE, HERE, AT CONSTRUCTION — never only at display.</b> It is
/// tempting to keep the raw body and let the display path clean it, since
/// <c>SanitizedErrorDescription</c> is what feeds the unauthenticated <c>/api/status</c>. That is
/// not enough, and the reason is <see cref="System.Exception.Message"/>: an exception's message and
/// <c>ToString()</c> are written by things NOBODY ROUTES — a generic <c>catch</c> that logs, the
/// circuit breaker's own logging, an unhandled-exception handler — and since #65 those land in a
/// persistent SQLite log store served at <c>/api/admin/logs</c>. A raw body held on this object
/// would reach that store through a path no display-side scrubber sits on. So the raw text never
/// becomes state: <see cref="BodyExcerpt"/> is already clean, and the message is built from the
/// clean value.</para>
///
/// <para>Scrubbing here does not make the display path redundant —
/// <c>SanitizedErrorDescription.Describe</c> still decides what reaches an unauthenticated surface
/// and still scrubs what it is given. Two layers, because this one protects the LOG and that one
/// protects the DASHBOARD, and neither is positioned to do the other's job.</para>
/// </summary>
public sealed class OllamaRequestException : HttpRequestException
{
    /// <summary>
    /// The most response-body text an excerpt may carry. Enough for Ollama's one-line
    /// <c>{"error":"…"}</c> shape, which is the whole point; short enough that a misbehaving
    /// endpoint cannot grow a persisted health field.
    /// </summary>
    public const int MaxExcerptLength = 200;

    /// <summary>
    /// The first <see cref="MaxExcerptLength"/> characters of the response body, whitespace
    /// collapsed and SCRUBBED of hosts, addresses and credentials; an empty string when the body
    /// was empty, unreadable, or scrubbed away entirely.
    ///
    /// <para>Safe to log. Still not safe to assume unbounded: it is capped, and the display path
    /// scrubs again on its own account.</para>
    /// </summary>
    public string BodyExcerpt { get; }

    /// <param name="statusCode">The status Ollama answered with.</param>
    /// <param name="bodyExcerpt">
    /// The response-body excerpt. Scrubbed and capped HERE, so a caller may pass raw upstream text
    /// and no raw text survives onto the object or into <see cref="System.Exception.Message"/>.
    /// </param>
    /// <remarks>
    /// The ONLY constructor, deliberately: every route onto this object therefore passes through
    /// <see cref="Clean"/>. A second one taking an already-clean value is how the raw body would
    /// eventually get back in.
    /// </remarks>
    public OllamaRequestException(HttpStatusCode statusCode, string bodyExcerpt)
        : this(statusCode, bodyExcerpt, Clean(bodyExcerpt))
    {
    }

    /// <summary>
    /// Exists only so <see cref="Clean"/> runs ONCE and its result reaches both the base message and
    /// the property. Private and unreachable except through the constructor above, which is what
    /// keeps "the only way in scrubs" true.
    /// </summary>
    private OllamaRequestException(HttpStatusCode statusCode, string _, string cleanExcerpt)
        : base(BuildMessage(statusCode, cleanExcerpt), inner: null, statusCode)
    {
        BodyExcerpt = cleanExcerpt;
    }

    /// <summary>Caps the body, then scrubs it. Cap first: the scrubbers are regex passes, and
    /// bounding their input keeps an oversized body from becoming an oversized scan.</summary>
    private static string Clean(string? bodyExcerpt) =>
        Diagnostics.SanitizedErrorDescription.ScrubForPublication(Excerpt(bodyExcerpt));

    /// <summary>
    /// The message carries the SCRUBBED excerpt rather than omitting it. This is what an operator
    /// reads in the log, and it is the detail that says which 400 this was — withholding it here
    /// would leave the log as uninformative as the dashboard used to be, for no security gain now
    /// that the value is clean.
    /// </summary>
    private static string BuildMessage(HttpStatusCode statusCode, string scrubbedExcerpt) =>
        string.IsNullOrWhiteSpace(scrubbedExcerpt)
            ? $"Ollama returned {(int)statusCode} {statusCode}."
            : $"Ollama returned {(int)statusCode} {statusCode}: {scrubbedExcerpt}";

    /// <summary>
    /// Reads the failed <paramref name="response"/>'s body into a capped excerpt and builds the
    /// exception. Must be called while the response is still open.
    ///
    /// <para>A body that cannot be read yields an EMPTY excerpt rather than propagating a second
    /// failure: the status code is already known and is the more important half, and a read fault
    /// while handling a request fault would replace a useful report with an unrelated one.</para>
    /// </summary>
    public static async Task<OllamaRequestException> FromResponseAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(response);

        var statusCode = response.StatusCode;
        string body;
        try
        {
            body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            body = string.Empty;
        }

        // Handed over RAW: the constructor caps and scrubs, and it is the only thing that does, so
        // there is one place to look for what this type guarantees about its own contents.
        return new OllamaRequestException(statusCode, body);
    }

    /// <summary>
    /// Collapses runs of whitespace (a body split across lines becomes one readable line in a
    /// single-line health field) and truncates to the cap.
    /// </summary>
    private static string Excerpt(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return string.Empty;
        }

        var collapsed = string.Join(' ', body.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        return collapsed.Length <= MaxExcerptLength
            ? collapsed
            : collapsed[..MaxExcerptLength];
    }
}
