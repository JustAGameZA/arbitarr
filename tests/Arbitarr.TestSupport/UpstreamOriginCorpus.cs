namespace Arbitarr.TestSupport;

/// <summary>
/// The ONE adversarial corpus of upstream-origin URL forms, driven at BOTH boundaries — the write
/// boundary (<c>SourceRepository.ValidateBaseUrl</c>, in <c>Arbitarr.Data.Tests</c>) and the feed
/// link pin (<c>TorznabFeedParser.TryValidateOriginPinnedLink</c>, in <c>Arbitarr.Core.Tests</c>).
///
/// <para><b>Why one shared corpus rather than one per boundary.</b> arb-4vzm, arb-07ei and arb-iub9
/// were three symptoms of the same defect: the two boundaries disagreed about what a legitimate
/// origin is, and each gap between their answers was a bug. Three corpora would have preserved
/// exactly that — a form added to one list and not the other is a gap reopening. With one list, a
/// form added later is automatically tested against both.</para>
///
/// <para>This type holds DATA ONLY and deliberately references no Arbitarr project, so it can live
/// in <c>Arbitarr.TestSupport</c> (whose csproj comment records that rule) and be consumed by test
/// assemblies on either side of the <c>Core</c>/<c>Data</c> boundary.</para>
///
/// <para>Fixtures use <c>indexer.example</c> / <c>attacker.example</c> and the RFC 3849
/// documentation range <c>2001:db8::/32</c> only.</para>
/// </summary>
public static class UpstreamOriginCorpus
{
    /// <summary>The password planted in every credential-bearing form; the string a leak test hunts for.</summary>
    public const string PlantedPassword = "pl4nted-corpus-password";

    /// <summary>The origin the pin cases are measured against unless a case names its own.</summary>
    public const string DefaultOrigin = "https://indexer.example:9117";

    /// <summary>
    /// Base-URL forms and whether <c>ValidateBaseUrl</c> must ACCEPT them. Each row carries the
    /// reason, so a failure names the property rather than just the string.
    /// </summary>
    public static IEnumerable<object[]> BaseUrlCases()
    {
        // --- userinfo: every form must be refused (arb-4vzm) -------------------------------------
        yield return Row($"https://user:{PlantedPassword}@indexer.example:9117", false, "userinfo: user and password");
        yield return Row("https://user@indexer.example:9117", false, "userinfo: username only, no password");
        yield return Row($"https://%75ser:{PlantedPassword}@indexer.example:9117", false, "userinfo: percent-encoded username (Uri decodes %75 to 'u')");
        yield return Row($"https://user:{PlantedPassword}%40@indexer.example:9117", false, "userinfo: encoded '@' inside the password");
        yield return Row("https://user:@indexer.example:9117", false, "userinfo: empty password, still a credential component");

        // --- trailing dot (arb-iub9) --------------------------------------------------------------
        yield return Row("http://indexer.example.:9117", false, "trailing root dot: resolves the same but never compares equal at the pin");
        // The double dot is not RFC-legal and Uri.TryCreate refuses it outright, so it is caught by
        // the parse floor rather than by the trailing-dot arm. Pinned so a future reader knows which
        // arm owns it.
        yield return Row("http://indexer.example..:9117", false, "double trailing dot: Uri.TryCreate refuses it, caught by the parse floor");

        // --- scheme floor -------------------------------------------------------------------------
        yield return Row("ftp://indexer.example:9117", false, "non-http(s) scheme");
        yield return Row("indexer.example:9117", false, "not an absolute URL");
        yield return Row("", false, "empty");

        // --- .invalid (arb-c29, pre-existing, must stay refused) ----------------------------------
        yield return Row("http://indexer.example.invalid:9117", false, "RFC 2606 .invalid host");

        // --- accepted forms: the positive controls ------------------------------------------------
        yield return Row("https://indexer.example:9117", true, "plain https with an explicit port");
        yield return Row("http://indexer.example:9117", true, "plain http with an explicit port");
        yield return Row("https://indexer.example", true, "implicit default port");
        yield return Row("https://indexer.example:443", true, "explicit default port");
        // Uri lower-cases the scheme and the host, so this is the same value as the plain form. Pinned
        // rather than assumed: the arb-x7w8.20 audit found an uppercase form escaping a different check.
        yield return Row("HTTPS://INDEXER.EXAMPLE:9117", true, "uppercase scheme and host, normalised by Uri");
        // Uri STRIPS leading whitespace (verified; the arb-x7w8.20 audit found the same on ApiPath),
        // so these parse to the plain form and are accepted. Pinned because it is surprising.
        yield return Row(" https://indexer.example:9117", true, "leading space, stripped by Uri");
        yield return Row("\thttps://indexer.example:9117", true, "leading tab, stripped by Uri");
        yield return Row("http://[2001:db8::1]:9117", true, "IPv6 literal (RFC 3849 documentation range)");
        yield return Row("http://xn--dmin-moa0i.example", true, "IDN A-label (punycode)");
        yield return Row("http://dömäin.example", true, "IDN U-label (Unicode)");
        yield return Row("https://indexer.example:9117/base/path", true, "a path is not an origin concern");

        static object[] Row(string baseUrl, bool accepted, string because) => [baseUrl, accepted, because];
    }

    /// <summary>
    /// Every base-URL form the corpus says <c>ValidateBaseUrl</c> must ACCEPT, as a single argument.
    /// Drives the agreement property (S7): for each, a link at that exact origin must pass the pin.
    /// </summary>
    public static IEnumerable<object[]> AcceptedBaseUrls() =>
        BaseUrlCases().Where(row => (bool)row[1]).Select(row => new[] { row[0] });

    /// <summary>
    /// Link/origin pairs and whether the pin must ACCEPT the link. Ordered so each refusal sits
    /// beside the accepting form it differs from by exactly one component.
    /// </summary>
    public static IEnumerable<object[]> PinCases()
    {
        // --- the positive controls: a matching link is still accepted (S5) -------------------------
        yield return Row("https://indexer.example:9117/dl?id=1", DefaultOrigin, true, "same scheme, host and port");
        yield return Row("https://INDEXER.EXAMPLE:9117/dl", DefaultOrigin, true, "host comparison is case-insensitive, as DNS is");
        yield return Row("HTTPS://indexer.example:9117/dl", DefaultOrigin, true, "Uri lower-cases the scheme, so an uppercase one still matches");
        yield return Row("https://indexer.example/dl", "https://indexer.example:443", true, "implicit 443 matches an explicit 443 (Uri.Port, not Authority text)");
        yield return Row("https://indexer.example:443/dl", "https://indexer.example", true, "and the mirror of that");

        // --- scheme (arb-07ei), asserted PER DIRECTION --------------------------------------------
        yield return Row("http://indexer.example:9117/dl", DefaultOrigin, false, "downgrade: http link against an https origin");
        yield return Row("https://indexer.example:9117/dl", "http://indexer.example:9117", false, "upgrade: https link against an http origin");
        yield return Row("ftp://indexer.example:9117/dl", "ftp://indexer.example:9117", false, "the http(s) floor holds even when the origin itself is ftp");

        // --- userinfo in the LINK (arb-07ei) ------------------------------------------------------
        yield return Row($"https://user:{PlantedPassword}@indexer.example:9117/dl", DefaultOrigin, false, "userinfo: Uri.Host excludes it, so host+port alone matched");
        yield return Row("https://user@indexer.example:9117/dl", DefaultOrigin, false, "userinfo: username only");
        yield return Row($"https://%75ser:{PlantedPassword}@indexer.example:9117/dl", DefaultOrigin, false, "userinfo: percent-encoded username");
        yield return Row($"https://user:{PlantedPassword}%40@indexer.example:9117/dl", DefaultOrigin, false, "userinfo: encoded '@' inside the password");
        yield return Row("https://user:@indexer.example:9117/dl", DefaultOrigin, false, "userinfo: empty password");

        // --- host and port ------------------------------------------------------------------------
        yield return Row("https://attacker.example:9117/dl", DefaultOrigin, false, "foreign host");
        yield return Row("https://indexer.example:9118/dl", DefaultOrigin, false, "genuine port mismatch");

        // --- trailing dot, both directions --------------------------------------------------------
        // Neither can arise from a STORED origin any more (ValidateBaseUrl refuses the dot form), but
        // the link side is upstream-supplied and therefore still reachable. Pinned so the one policy
        // stays in ValidateBaseUrl: the pin does no trailing-dot handling of its own.
        yield return Row("https://indexer.example.:9117/dl", DefaultOrigin, false, "dotted link against a dot-free origin");
        yield return Row("https://indexer.example:9117/dl", "https://indexer.example.:9117", false, "dot-free link against a dotted origin (unreachable via a stored row)");

        // --- IPv6 (RFC 3849 documentation range) ---------------------------------------------------
        yield return Row("http://[2001:db8::1]:9117/dl", "http://[2001:db8::1]:9117", true, "IPv6 literal, identical text");
        // DECISION, not an accident: Uri.Host canonicalises an IPv6 literal (it compresses the
        // address while keeping the brackets), so the expanded and compressed spellings of the SAME
        // address DO compare equal here. Pinned so a future change to a raw textual comparison is
        // caught by a failing test rather than by an operator whose links stop matching.
        yield return Row("http://[2001:0db8:0000:0000:0000:0000:0000:0001]:9117/dl", "http://[2001:db8::1]:9117", true, "IPv6: Uri.Host canonicalises, so the expanded spelling matches");
        yield return Row("http://[2001:db8::2]:9117/dl", "http://[2001:db8::1]:9117", false, "a genuinely different IPv6 address");

        // --- IDN -----------------------------------------------------------------------------------
        // MEASURED, and deliberately left as it is: Uri.Host returns the U-label for a Unicode input
        // and the A-label for a punycode input, so the two spellings of the same host do NOT compare
        // equal. Uri.IdnHost would unify them; changing only the pin would make the two boundaries
        // disagree again, which is the defect this component closes, and changing both is a policy
        // change rather than a bug fix.
        yield return Row("http://xn--dmin-moa0i.example/dl", "http://xn--dmin-moa0i.example", true, "IDN A-label against itself");
        yield return Row("http://dömäin.example/dl", "http://dömäin.example", true, "IDN U-label against itself");
        yield return Row("http://dömäin.example/dl", "http://xn--dmin-moa0i.example", false, "IDN: Uri.Host does not fold U-label onto A-label");

        // --- malformed -----------------------------------------------------------------------------
        yield return Row("/relative/dl", DefaultOrigin, false, "not an absolute URL");
        yield return Row("", DefaultOrigin, false, "empty");

        static object[] Row(string link, string origin, bool accepted, string because) => [link, origin, accepted, because];
    }
}
