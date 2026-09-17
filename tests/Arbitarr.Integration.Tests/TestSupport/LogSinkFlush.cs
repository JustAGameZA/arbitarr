using Arbitarr.Data.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Arbitarr.Integration.Tests.TestSupport;

/// <summary>
/// Drains a test host's log sink DETERMINISTICALLY via <see cref="SqliteLoggerProvider.FlushAsync"/>,
/// which exists for exactly this: the pump batches on <see cref="SqliteLoggerProvider.FlushInterval"/>
/// and must never write on the caller's thread, so a read taken immediately after a log call can
/// legitimately see nothing yet (arb-d4h8).
///
/// <para><b>Why one shared extension rather than a member on a single factory type.</b> The fifteen
/// call sites build their host through three different mechanisms — <c>ArbitarrWebApplicationFactory</c>,
/// a plain <c>WebApplicationFactory&lt;Program&gt;</c> (sometimes via <c>WithWebHostBuilder</c>), and
/// <c>RemoteAddressWebApplicationFactory</c> — so a method on any one of those types could not cover
/// the others without a second, drifting implementation. An extension on <see cref="IServiceProvider"/>
/// covers all three: every caller already has a <c>Services</c> property to hand it.</para>
///
/// <para><b>Preferred over sleeping out the interval</b> — a <c>Task.Delay</c> long enough to be
/// reliable is a per-assertion cost the whole suite pays, and one short enough to be cheap is a
/// flake; that is exactly the defect this replaces (arb-s3ky, #471). Every assertion at each call
/// site is preceded by a non-empty check, so a failure to drain surfaces as a loud failure rather
/// than a vacuous pass either way.</para>
///
/// <para><b>Throws if no <see cref="SqliteLoggerProvider"/> is registered.</b> A flush that silently
/// does nothing when the sequence is empty would turn every caller back into exactly the race this
/// replaces — the empty-sequence case is indistinguishable from "already flushed" to a caller that
/// never checks, and the fixed-delay bug this fixes was itself invisible until it flaked under CI
/// load. Failing loud here means a host that stops registering the provider breaks this helper's
/// callers immediately, not the next time CI happens to be slow.</para>
/// </summary>
public static class LogSinkFlush
{
    /// <summary>
    /// Awaits <see cref="SqliteLoggerProvider.FlushAsync"/> on every <see cref="SqliteLoggerProvider"/>
    /// registered against <paramref name="services"/>. Pass the <c>Services</c> of whichever host's
    /// log rows the caller is about to read — where a test builds several hosts, that is the one
    /// under assertion, not necessarily the one it constructed first.
    /// </summary>
    public static async Task FlushLogSinkAsync(this IServiceProvider services)
    {
        var providers = services.GetServices<ILoggerProvider>().OfType<SqliteLoggerProvider>().ToArray();

        if (providers.Length == 0)
        {
            throw new InvalidOperationException(
                "No SqliteLoggerProvider is registered on this host; flushing it would silently " +
                "flush nothing and hand the caller back the exact race this helper exists to remove.");
        }

        foreach (var provider in providers)
        {
            await provider.FlushAsync();
        }
    }
}
