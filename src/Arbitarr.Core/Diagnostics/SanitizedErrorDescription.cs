namespace Arbitarr.Core.Diagnostics;

/// <summary>
/// Reduces an exception to a topology-safe description for any field that reaches an
/// unauthenticated surface: the exception type name, plus the HTTP status code when the exception
/// carries one.
/// </summary>
/// <remarks>
/// <see cref="System.Exception.Message"/> is never surfaced. For an
/// <see cref="System.Net.Http.HttpRequestException"/> raised by a DNS/connect failure or by
/// <c>EnsureSuccessStatusCode</c>, the message text routinely embeds the upstream host
/// (e.g. "No such host is known. (host:5076)"). Both <c>CircuitBreakerSnapshot.LastError</c> and
/// <c>RefreshWorkerHealthSnapshot.LastError</c> are served verbatim by the unauthenticated
/// <c>GET /api/status</c> dashboard, so every write to either must pass through here — a raw
/// <c>ex.Message</c> on those paths leaks LAN topology to any unauthenticated caller.
/// Full exception detail (message and stack) still reaches the operator through the logger.
/// </remarks>
public static class SanitizedErrorDescription
{
    /// <summary>Describes <paramref name="ex"/> without echoing its message text.</summary>
    public static string Describe(Exception ex) => ex switch
    {
        System.Net.Http.HttpRequestException { StatusCode: { } statusCode } =>
            $"{nameof(System.Net.Http.HttpRequestException)} ({(int)statusCode} {statusCode})",
        _ => ex.GetType().Name,
    };
}
