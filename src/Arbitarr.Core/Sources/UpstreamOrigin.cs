namespace Arbitarr.Core.Sources;

/// <summary>
/// The single answer to "what is a legitimate upstream origin, and is this URL at it?", shared by
/// the write boundary (<c>Arbitarr.Data.Sources.SourceRepository.ValidateBaseUrl</c>) and the
/// feed-link pin (<see cref="TorznabFeedParser.TryValidateOriginPinnedLink"/>).
///
/// <para><b>Why one component rather than two checks that agree by convention.</b> This is the same
/// argument <see cref="TorznabFeedParser"/>'s class doc makes about the wire parser ("two copies of
/// a wire parser are two things to keep true, and the one that drifts is the one nobody notices").
/// Here the two copies had already drifted, in three directions at once: the write boundary accepted
/// userinfo the pin then ignored (arb-4vzm), the pin compared host and port but not scheme so a feed
/// could downgrade the download fetch to cleartext (arb-07ei), and the write boundary accepted a
/// trailing-dot host that the pin then rejected for every link the indexer returned, giving a source
/// that is configured, empty, and silent about why (arb-iub9). Each is the gap between the two
/// answers, so closing them one at a time would have left three places to keep agreeing.</para>
///
/// <para><b>The property that must hold:</b> for every value the write boundary accepts as a base
/// URL, a link at that exact origin passes the pin. <c>SourceRepositoryTests</c> asserts it over the
/// shared corpus; it is what a fourth escape form would break.</para>
///
/// <para>Two shapes are exposed on purpose, because the callers need different failure modes. The
/// write boundary wants a reason it can turn into a 400, and gets <see cref="DescribeBaseUrlFault"/>
/// returning a message its own exception type carries. The pin wants a bool so
/// <see cref="TorznabFeedParser.ParseFeedResponse"/> can drop the item; an exception per dropped
/// item is a cost a 100-item feed should not pay.</para>
/// </summary>
public static class UpstreamOrigin
{
    /// <summary>
    /// The message the write boundary reports for a URL that carries userinfo.
    ///
    /// <para><b>It deliberately does not echo the value.</b> The value is the credential: putting it
    /// in the rejection would write the secret into the response body, the operator's screen, and
    /// any log line that records the failure — performing the leak the check exists to prevent.
    /// Worded after <c>ArrInstanceRepository.ValidateBaseUrl</c>'s sibling rejection rather than
    /// <c>SettingsValidator.ValidateOllamaBaseUrl</c>'s, whose reasoning is specific to Ollama
    /// having no authentication at all.</para>
    /// </summary>
    public const string UserInfoFault =
        "The source base URL must not contain credentials (user:password@host). Store the indexer " +
        "key in the source's API key field instead, where it is write-only.";

    /// <summary>
    /// Classifies a candidate base URL, returning <c>null</c> when it is acceptable and otherwise a
    /// human-readable reason. The caller supplies its own exception type; this returns text.
    ///
    /// <para><b>The order of the arms is load-bearing, not stylistic.</b> The userinfo arm must come
    /// before every arm that echoes <paramref name="baseUrl"/>, or a credential-bearing URL that is
    /// ALSO (say) a <c>.invalid</c> host would leak its credential through the other arm's message.
    /// Do not re-sort these.</para>
    ///
    /// <para>The <c>.invalid</c> arm itself stays with the caller (<c>SourceRepository</c>), because
    /// its reasoning is about seeding a placeholder, not about what an origin is.</para>
    /// </summary>
    /// <param name="baseUrl">The operator-supplied value.</param>
    /// <param name="parsed">The parsed URI when the return value is <c>null</c>; otherwise <c>null</c>.</param>
    public static string? DescribeBaseUrlFault(string? baseUrl, out Uri? parsed)
    {
        parsed = null;

        if (string.IsNullOrWhiteSpace(baseUrl)
            || !Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return $"'{baseUrl}' is not a valid absolute http(s) URL.";
        }

        // MUST stay above every echoing arm below and in the caller. See the doc comment.
        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            return UserInfoFault;
        }

        // arb-iub9: the RFC-legal root dot. Uri keeps it in Host, DNS resolves it to the same host,
        // and the pin's ordinal host comparison then rejects every link the indexer returns — a
        // source that is configured, answers nothing, and says nothing about why, which is the shape
        // ADR 0014 exists to prevent.
        //
        // REFUSED rather than normalised away, deliberately. Normalising is the wrong shape here:
        // this is a validator, not a normaliser — it has no return channel for a corrected value, so
        // normalising would mean changing the signature and all three write paths, after which every
        // future reader has to know the stored BaseUrl is not the string the operator typed. Worse,
        // normalising at the write boundary would not close the defect at all: the pin compares
        // against whatever is already stored, so it would only close it for rows written after this
        // change. A refusal the operator sees at the moment they paste the value beats a degradation
        // they may not notice for weeks. Echoing the value is fine here and consistent with the
        // .invalid arm beside it — a trailing-dot hostname is not a credential.
        if (uri.Host.EndsWith('.'))
        {
            return $"'{baseUrl}' ends its host name with a trailing dot ('{uri.Host}'). Remove the " +
                   "trailing dot: it resolves to the same host but does not compare equal to the " +
                   "addresses the source returns, so every result would be discarded.";
        }

        parsed = uri;
        return null;
    }

    /// <summary>
    /// True when <paramref name="candidate"/> is an http(s) URL at exactly
    /// <paramref name="allowedOrigin"/> — same scheme, host and port — and carries no userinfo.
    ///
    /// <para>Host comparison is <see cref="StringComparison.OrdinalIgnoreCase"/> and must stay so:
    /// DNS names are case-insensitive, so <c>INDEXER.EXAMPLE</c> and <c>indexer.example</c> are the
    /// same host. Scheme is compared ordinally because <see cref="Uri"/> already lower-cases it.</para>
    ///
    /// <para>Port is compared as <see cref="Uri.Port"/>, not as text: <c>https://indexer.example</c>
    /// and <c>https://indexer.example:443</c> are the same origin and both report 443. Comparing
    /// <c>Authority</c> strings instead would break that.</para>
    ///
    /// <para><b>IPv6:</b> <see cref="Uri.Host"/> canonicalises the literal (it keeps the brackets but
    /// compresses the address), so <c>[2001:0db8:0000:0000:0000:0000:0000:0001]</c> and
    /// <c>[2001:db8::1]</c> DO compare equal here. That is a consequence of the framework's
    /// normalisation, not of this comparison; it is pinned by test so a future change to a raw
    /// textual comparison is caught.</para>
    ///
    /// <para><b>IDN:</b> <see cref="Uri.Host"/> returns the UNICODE form for a Unicode input and the
    /// A-label for a punycode input, so <c>http://dömäin.example</c> and
    /// <c>http://xn--dmin-moa0i.example</c> do NOT compare equal, even though they name the same
    /// host. Pinned by test. Comparing <see cref="Uri.IdnHost"/> would unify them; that is a
    /// deliberate non-change here, because doing it at the pin alone would make the pin and the
    /// write boundary disagree again — the exact defect this component exists to close — and doing
    /// it at both is a policy change, not a bug fix.</para>
    /// </summary>
    public static bool IsAtOrigin(Uri candidate, Uri allowedOrigin)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(allowedOrigin);

        if (candidate.Scheme != Uri.UriSchemeHttp && candidate.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        // arb-07ei: userinfo is invisible to Uri.Host, so a link of the form
        // http://user:pw@indexer.example:9117/x matched on host+port alone. Accepting it would let a
        // hostile feed inject Basic-auth credentials into a URL Arbitarr subsequently fetches.
        if (!string.IsNullOrEmpty(candidate.UserInfo))
        {
            return false;
        }

        // arb-07ei: the scheme. Without it an https origin accepts an http link, downgrading the
        // download fetch — which carries the indexer key — to cleartext.
        return string.Equals(candidate.Scheme, allowedOrigin.Scheme, StringComparison.Ordinal)
            && string.Equals(candidate.Host, allowedOrigin.Host, StringComparison.OrdinalIgnoreCase)
            && candidate.Port == allowedOrigin.Port;
    }
}
