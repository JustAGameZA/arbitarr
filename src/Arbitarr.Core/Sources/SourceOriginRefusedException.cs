namespace Arbitarr.Core.Sources;

/// <summary>
/// Thrown when a source's configured API path resolves to an endpoint that is not on the origin of
/// its configured base URL, so constructing the source is refused. The source's API key is sent to
/// that endpoint, and an endpoint on another origin means handing the operator's key to a host they
/// never configured.
/// </summary>
/// <remarks>
/// <para>
/// <b>A dedicated type rather than <see cref="ArgumentException"/>, and that is load-bearing.</b>
/// <see cref="ArgumentNullException"/> IS an <see cref="ArgumentException"/>, and the constructor
/// that throws this also throws <see cref="ArgumentNullException"/> for its null guards with a
/// <c>paramName</c> that cannot be told apart. A caller that builds sources in a loop — the
/// registry (arb-x7w8.4) is exactly that shape — naturally writes a
/// <c>catch (ArgumentException)</c> arm to skip a misconfigured row and carry on. With the refusal
/// typed as <see cref="ArgumentException"/>, that arm would swallow a SECURITY refusal and treat it
/// as an ordinary bad-argument skip, silently downgrading it to the same severity as a missing
/// display name. Being outside the <see cref="ArgumentException"/> hierarchy means such an arm
/// cannot catch this by accident: a caller that wants to continue past it must name it and say so.
/// </para>
/// <para>
/// Carries <see cref="SourceName"/> and nothing else. The offending endpoint and the configured API
/// path are deliberately absent from both this type and its message: they name a host the operator
/// did not configure, the path may be shaped to carry attacker-chosen text, and an exception
/// message reaches the persistent log store served at <c>/api/admin/logs</c>.
/// </para>
/// </remarks>
public sealed class SourceOriginRefusedException : Exception
{
    public SourceOriginRefusedException(string sourceName)
        : base($"Source '{sourceName}': the configured API path resolves to an endpoint outside the scheme, host, port or "
               + "credentials of the configured base URL. The source's API key is sent to that endpoint, so it must stay on "
               + "the configured origin. Configure an API path relative to the base URL.")
    {
        SourceName = sourceName;
    }

    public string SourceName { get; }
}
