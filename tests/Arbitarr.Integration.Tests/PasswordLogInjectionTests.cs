using System.Net;
using System.Net.Http.Json;
using Arbitarr.Api.Admin;
using Arbitarr.Api.Security;
using Arbitarr.Core.Security;
using Arbitarr.Data.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Arbitarr.Integration.Tests;

/// <summary>
/// #96: neither password may reach the persistent log store (#65). The direct sibling of
/// <see cref="LogSecretInjectionTests"/>, which does the same job for a source API key.
///
/// <para><b>THE POSTURE IS STRUCTURAL, NOT A DENYLIST.</b> Nothing on the password path logs a
/// request body; <c>ChangePasswordRequest</c> carries no <c>ToString</c> override, so an
/// interpolated one could not print its fields either; and the handler logs no field of it.
/// <see cref="LogMessageCleanser"/> is the defence-in-depth layer beneath that, and it scrubs QUERY
/// STRINGS only — which is precisely why the password is accepted from a POST body and from nowhere
/// else. A posture is only real if something fails when it is broken; this is that something.</para>
///
/// <para><b>THE STORE READ HERE IS <c>arbitarr-logs.db</c>, NOT <c>arbitarr.db</c>.</b> There are
/// two SQLite databases and they are deliberately separate (see CLAUDE.md §1). Reading the config
/// database would search a file the log rows were never written to, and every assertion below would
/// pass for the wrong reason. The store is resolved through <see cref="LogStore"/> — grep for
/// <see cref="LogStore.DatabaseFileName"/>, never for the config database's name.</para>
/// </summary>
public sealed class PasswordLogInjectionTests
{
    private const string Username = "log-probe-operator";

    // Distinctive enough to be found anywhere in a log row, and obviously throwaway: this
    // repository is public and no real credential may appear in committed content.
    private const string CurrentPassword = "example-current-passphrase-probe";
    private const string NewPassword = "example-new-passphrase-probe";

    // The per-test config directory is owned by RemoteAddressWebApplicationFactory, which creates
    // its own and deletes it on dispose. This class used to declare a second one and point
    // ARBITARR_CONFIG_DIR at it, which never had any effect: the factory set the same process-wide
    // variable to ITS directory afterwards, so the host always ran against the factory's, and the
    // one declared here was created, never used, and then deleted. Isolation is unchanged; the
    // store read below is still this test's own, because the factory instance is.

    [Fact]
    public async Task Neither_password_appears_in_any_log_row_after_a_change_and_a_failed_change()
    {
        await using var factory = new RemoteAddressWebApplicationFactory(IPAddress.Loopback);

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(AdminApiKeyFilter.SessionRequestHeaderName, "1");

        using (var setup = await client.PostAsJsonAsync(
            AuthEndpoints.SetupRoute,
            new { username = Username, password = CurrentPassword }))
        {
            Assert.Equal(HttpStatusCode.Created, setup.StatusCode);

            var cookie = setup.Headers.GetValues("Set-Cookie")
                .Single(v => v.StartsWith(ISessionAuthenticator.CookieName + "=", StringComparison.Ordinal));
            client.DefaultRequestHeaders.Add("Cookie", cookie.Split(';', 2)[0]);
        }

        // A FAILED change first — an error path is where a request is most likely to be logged
        // verbatim, so a happy-path-only probe would miss the realistic leak.
        using (var refused = await client.PostAsJsonAsync(
            AuthEndpoints.PasswordRoute,
            new { currentPassword = "example-wrong-passphrase-probe", newPassword = NewPassword }))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        }

        // Then a SUCCESSFUL one, so both values have genuinely travelled through the handler.
        using (var changed = await client.PostAsJsonAsync(
            AuthEndpoints.PasswordRoute,
            new { currentPassword = CurrentPassword, newPassword = NewPassword }))
        {
            Assert.Equal(HttpStatusCode.NoContent, changed.StatusCode);
        }

        var store = factory.Services.GetRequiredService<LogStore>();

        // THE POSITIVE CONTROL, and the part that makes everything below bite. A value driven
        // through the SAME store on the SAME code path is scrubbed and lands carrying the
        // cleanser's replacement marker — which proves three things at once: the sink is writing,
        // this test can read what it wrote, and a secret that reached a log row would be VISIBLE to
        // the search below rather than silently absent. Without it, "neither password appears"
        // passes identically against a store that was never written to at all.
        var logger = factory.Services
            .GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>()
            .CreateLogger("Arbitarr.Test.PasswordProbeControl");
        logger.LogWarning(
            "upstream request failed: http://192.0.2.10:5076/api?t=search&apikey={0}",
            "secret-api-key-password-probe-control");

        await FlushLogSinkAsync();

        var page = await store.ReadAsync(level: null, logger: null, page: 1, pageSize: LogStore.MaxPageSize);

        Assert.NotEmpty(page.Entries);

        var control = page.Entries
            .Where(e => e.Logger.Contains("PasswordProbeControl", StringComparison.Ordinal))
            .ToList();
        Assert.NotEmpty(control);
        Assert.All(control, entry =>
            Assert.Contains(LogMessageCleanser.Replacement, entry.Message, StringComparison.Ordinal));

        // Now the real assertions, over EVERY field an entry has rather than the message alone: the
        // exception text is the field most likely to carry a request payload, and checking only
        // Message would let the most probable leak through while looking thorough.
        foreach (var entry in page.Entries)
        {
            Assert.DoesNotContain(CurrentPassword, entry.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(CurrentPassword, entry.Exception ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(CurrentPassword, entry.Logger, StringComparison.OrdinalIgnoreCase);

            Assert.DoesNotContain(NewPassword, entry.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(NewPassword, entry.Exception ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(NewPassword, entry.Logger, StringComparison.OrdinalIgnoreCase);
        }

        // AND NO PIPELINE ROW MAY CARRY THE CLEANSER'S MARKER. This assertion exists because the
        // absence assertions above are NOT sufficient on their own, and a mutation run outside the
        // repository is what established that rather than reasoning about it.
        //
        // LogMessageCleanser.NamedCredential matches a credential-shaped NAME followed by a value —
        // which is exactly the shape a leaking handler produces. Feed it the two most realistic
        // leaks, a logged JSON body ({"currentPassword":"…"}) or an interpolated record
        // (CurrentPassword = …), and the cleanser rewrites the value to <redacted> BEFORE the row is
        // stored. The password is then genuinely absent from the store and every assertion above
        // passes — against code that logged both passwords. Three shipped leaks (#57, #80, #78) had
        // precisely this shape of not-quite-vacuous assertion, and only mutation caught them.
        //
        // So the property asserted here is the stronger one: on this route nothing should reach the
        // cleanser AT ALL. A marker on a pipeline row means some call site put a credential-shaped
        // value into a log line and got rescued by the defence-in-depth layer — which is a bug in
        // the call site (see LogMessageCleanser's "the leak is the bug" note), not a pass.
        //
        // The control row is excluded BY LOGGER NAME, because it is the one row deliberately driven
        // through the cleanser to prove the store is written and readable at all. Excluding it by
        // name rather than by message keeps this assertion from being widened accidentally.
        var pipelineEntries = page.Entries
            .Where(e => !e.Logger.Contains("PasswordProbeControl", StringComparison.Ordinal))
            .ToList();

        Assert.NotEmpty(pipelineEntries);

        Assert.All(pipelineEntries, entry =>
        {
            Assert.DoesNotContain(LogMessageCleanser.Replacement, entry.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(LogMessageCleanser.Replacement, entry.Exception ?? string.Empty, StringComparison.Ordinal);
        });
    }

    /// <summary>
    /// Waits for the sink's background pump to drain — it batches on a fixed interval by design and
    /// must never write on the caller's thread, so a read taken immediately after a request can
    /// legitimately see nothing yet. See <see cref="LogSecretInjectionTests"/>, which does the same.
    /// </summary>
    private static async Task FlushLogSinkAsync() =>
        await Task.Delay(SqliteLoggerProvider.FlushInterval + TimeSpan.FromMilliseconds(750));

}
