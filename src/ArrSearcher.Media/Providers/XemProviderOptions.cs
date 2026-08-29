namespace ArrSearcher.Media.Providers;

/// <summary>
/// Configuration for <see cref="XemProvider"/>.
/// </summary>
/// <param name="BaseUrl">Base URL of the XEM service, e.g. https://thexem.info/. No API key required (public/keyless service).</param>
/// <param name="RequestTimeout">Per-HTTP-request timeout. Defaults to 10s so a XEM outage surfaces quickly as "source unreachable" rather than hanging.</param>
public sealed record XemProviderOptions(
    Uri BaseUrl,
    TimeSpan? RequestTimeout = null)
{
    public TimeSpan EffectiveRequestTimeout => RequestTimeout ?? TimeSpan.FromSeconds(10);
}
