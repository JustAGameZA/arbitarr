using System.Reflection;
using System.Text.RegularExpressions;
using Arbitarr.Core.Diagnostics;
using Xunit;

namespace Arbitarr.Core.Tests;

/// <summary>
/// arb-6vf: the shared credential redaction, tested as its own unit.
///
/// <para><b>Every row carries its positive control</b> (CLAUDE.md §4). An
/// <c>Assert.DoesNotContain(secret, output)</c> passes just as happily when the secret was never in
/// play, so each case first asserts the planted value IS in the input, then that
/// <see cref="CredentialPatterns.Replacement"/> IS in the output — which proves the pattern fired
/// and the value was removed, rather than the value simply never having arrived.</para>
///
/// <para>The cross-sink check — that both <c>LogMessageCleanser</c> and
/// <see cref="SanitizedErrorDescription"/> produce the same redaction from the same corpus — lives
/// in <c>Arbitarr.Data.Tests</c>, because that is the only test project that can reference both.
/// This file pins the shared implementation itself.</para>
///
/// <para>All fixture values are placeholders (PUBLIC repo): no real key, host or address.</para>
/// </summary>
public class CredentialPatternsTests
{
    /// <summary>
    /// The shared corpus. Each row is (input, the planted secret that must not survive). Kept as
    /// one collection so the cross-sink test in Data.Tests can assert the SAME shapes without the
    /// two lists drifting — which is the whole failure mode arb-6vf exists to remove.
    /// </summary>
    public static TheoryData<string, string> CredentialCorpus() => new()
    {
        // Query parameter, both spellings of the name.
        { "GET /api/search?apikey=PLACEHOLDERKEY123456 failed", "PLACEHOLDERKEY123456" },
        { "GET /api/search?api_key=PLACEHOLDERKEY123456 failed", "PLACEHOLDERKEY123456" },
        { "callback?token=PLACEHOLDERTOKEN9876 rejected", "PLACEHOLDERTOKEN9876" },
        { "grab?passkey=PLACEHOLDERPASS5544 denied", "PLACEHOLDERPASS5544" },
        // Authorization schemes.
        { "Authorization: Bearer PLACEHOLDERBEARER0011 was refused", "PLACEHOLDERBEARER0011" },
        { "Authorization: Basic UExBQ0VIT0xERVI6UEFTUw== was refused", "UExBQ0VIT0xERVI6UEFTUw==" },
        // Named credential, separator forms.
        { "X-Admin-Api-Key: PLACEHOLDERADMIN77 rejected", "PLACEHOLDERADMIN77" },
        { "{\"password\": \"PLACEHOLDERPW88\"}", "PLACEHOLDERPW88" },
        { "apikey=PLACEHOLDERINLINE99 in config", "PLACEHOLDERINLINE99" },
        { "client_secret=PLACEHOLDERSECRET12 expired", "PLACEHOLDERSECRET12" },
        // arb-cia3: the plaintext* arm. "PlaintextKey" matched NO word in the alternation before
        // this bead — "key" alone is deliberately excluded from it — so a synthesised record
        // ToString of CreatedApiKey / CreatedApiKeyResponse carried a live admin key through BOTH
        // sinks verbatim. Measured against the unmodified regex these two rows fail and every
        // other row still passes; with the arm added all of them pass. Both separator spellings,
        // because the record form renders " = " while a config line renders ":" or "=".
        { "CreatedApiKey { Entry = ApiKeyEntry, PlaintextKey = PLACEHOLDERPLAIN1234 }", "PLACEHOLDERPLAIN1234" },
        { "plaintext-key: PLACEHOLDERPLAIN5678 minted", "PLACEHOLDERPLAIN5678" },
        // Space-separated prose form. This arm existed ONLY in the status scrubber before arb-6vf;
        // it is in the shared corpus now precisely because the log cleanser was missing it.
        { "invalid key PLACEHOLDERSPACED3344 supplied", "PLACEHOLDERSPACED3344" },
        { "rejected token PLACEHOLDERSPACED5566 at gateway", "PLACEHOLDERSPACED5566" },
        // arb-qj9: '.' and '+' in the value class. Before that widening the run stopped at the first
        // '.', so the HIGH-ENTROPY TAIL stayed published beside a redaction of the first fragment —
        // which is why the planted token here is that tail, not the whole key.
        { "invalid key sk.live+PLACEHOLDER.9f8e supplied", "PLACEHOLDER.9f8e" },
        { "rejected token ey.PLACEHOLDER+payload.0011 at gateway", "PLACEHOLDER+payload.0011" },
    };

    [Theory]
    [MemberData(nameof(CredentialCorpus))]
    public void Every_credential_shape_is_redacted(string input, string secret)
    {
        // POSITIVE CONTROL: the planted value really is in the text being scrubbed, so the absence
        // assertion below is about the pattern working rather than about an empty set.
        Assert.Contains(secret, input, StringComparison.Ordinal);

        var redacted = CredentialPatterns.RedactCredentials(input);

        // Detectability: the pattern fired. Without this, deleting every pattern would still pass
        // the DoesNotContain below for any input that happened not to contain the secret.
        Assert.Contains(CredentialPatterns.Replacement, redacted, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, redacted, StringComparison.Ordinal);
    }

    /// <summary>
    /// arb-7j5x: EVERY arm must redact a SECOND occurrence of the SAME shape, not just its first.
    ///
    /// <para><b>Why this is a separate theory and not extra rows in
    /// <see cref="CredentialCorpus"/>.</b> Every row of that corpus plants exactly ONE value per
    /// shape, so a mutant in which each arm replaces only its FIRST match satisfies every absence
    /// assertion in the file — measured: it survived all of them. The corpus cannot simply gain a
    /// second value because <see cref="Redaction_is_idempotent"/> and the cross-sink theory in
    /// <c>Arbitarr.Data.Tests</c> consume the same rows for different properties; this is the one
    /// property that needs two occurrences, so it gets its own rows.</para>
    ///
    /// <para><b>The planted secret is the fragment MEASURED to survive the mutant, not simply "the
    /// second value".</b> That distinction is load-bearing, and the query arm is why. Writing the
    /// two credentials as adjacent parameters of ONE url (<c>?apikey=A&amp;token=B</c>) looks like
    /// the natural plant and is VACUOUS: under the mutant that row still ends with no <c>B</c> in
    /// the output, so it passes and proves nothing. Two SEPARATE query occurrences are used
    /// instead, which no later arm mops up. Re-measure before changing any of these strings.</para>
    /// </summary>
    public static TheoryData<string, string> SecondOccurrenceCorpus() => new()
    {
        // Query parameter: two separate occurrences, per the note above.
        {
            "GET /a?apikey=PLACEHOLDERKEY123456 then GET /b?apikey=PLACEHOLDERSECOND7788",
            "PLACEHOLDERSECOND7788"
        },
        // Authorization scheme.
        {
            "Authorization: Bearer PLACEHOLDERBEARER0011 retried as Bearer PLACEHOLDERBEARER2233",
            "PLACEHOLDERBEARER2233"
        },
        // Named credential.
        {
            "X-Admin-Api-Key: PLACEHOLDERADMIN77 and client_secret=PLACEHOLDERSECRET12 expired",
            "PLACEHOLDERSECRET12"
        },
        // Space-separated prose form.
        {
            "invalid key PLACEHOLDERSPACED3344 supplied, rejected token PLACEHOLDERSPACED5566 at gateway",
            "PLACEHOLDERSPACED5566"
        },
    };

    [Theory]
    [MemberData(nameof(SecondOccurrenceCorpus))]
    public void A_second_occurrence_of_the_same_shape_is_redacted_too(string input, string secret)
    {
        // POSITIVE CONTROL: the second occurrence really is in the input.
        Assert.Contains(secret, input, StringComparison.Ordinal);

        var redacted = CredentialPatterns.RedactCredentials(input);

        Assert.Contains(CredentialPatterns.Replacement, redacted, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, redacted, StringComparison.Ordinal);
    }

    /// <summary>
    /// The NAME survives while the value does not. "apikey=&lt;redacted&gt;" tells an operator which
    /// credential was present; bare "&lt;redacted&gt;" would leave them unable to tell an api key
    /// from a password. This pins the prefix-preserving replace, which a naive
    /// <c>Replace(text, Replacement)</c> would silently drop.
    /// </summary>
    [Fact]
    public void The_credential_name_survives_and_only_the_value_is_removed()
    {
        var redacted = CredentialPatterns.RedactCredentials("X-Admin-Api-Key: PLACEHOLDERADMIN77 rejected");

        Assert.Contains("X-Admin-Api-Key", redacted, StringComparison.Ordinal);
        Assert.Contains(CredentialPatterns.Replacement, redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("PLACEHOLDERADMIN77", redacted, StringComparison.Ordinal);
    }

    /// <summary>
    /// NEGATIVE THEORY — the over-redaction probe. A cleanser that redacted everything would pass
    /// every row above; these are the ordinary strings both sinks must stay readable for. A log line
    /// or a dashboard message redacted into uselessness is its own outage.
    /// </summary>
    [Theory]
    [InlineData("token expired")]                       // prose: below the 12-char value floor
    [InlineData("HTTP 400 Bad Request")]
    [InlineData("model llama3.1:8b not found")]
    [InlineData("request took 12:34:56 789 ms")]
    [InlineData("commit 9f8e7d6c5b4a3f2e1d0c9b8a7f6e5d4c3b2a1f09")]
    [InlineData("release guid 0b5f2c1e-7a3d-4f10-9c8b-2e6a4d5f1b30")]
    // arb-cia3: the word "plaintext" is ordinary prose in this codebase (three probe outcomes say
    // "https pointed at a plaintext port"), so the new arm must NOT fire on it. It does not,
    // because the alternative requires a credential NOUN after it — "plaintext" alone is not in
    // the alternation, only "plaintextkey"/"plaintext_token"/… are. Measured: a bare "plaintext"
    // alternative redacted "TLS disabled, plaintext: true" and "plaintext: unavailable"; the
    // noun-suffixed one leaves both intact. These rows are what keeps it that way.
    [InlineData("https pointed at a plaintext port")]
    [InlineData("stored in plaintext for now")]
    [InlineData("TLS disabled, plaintext: true")]
    [InlineData("plaintext: unavailable")]
    public void Ordinary_text_is_left_intact(string input)
    {
        Assert.Equal(input, CredentialPatterns.RedactCredentials(input));
    }

    /// <summary>
    /// Idempotent: scrubbing already-scrubbed text changes nothing. The status scrubber runs over a
    /// value <c>OllamaRequestException</c> already scrubbed at construction, so this property is
    /// relied on rather than merely nice — and it also proves the replacement token itself cannot be
    /// re-captured as a credential value.
    /// </summary>
    [Theory]
    [MemberData(nameof(CredentialCorpus))]
    public void Redaction_is_idempotent(string input, string secret)
    {
        Assert.Contains(secret, input, StringComparison.Ordinal);

        var once = CredentialPatterns.RedactCredentials(input);
        var twice = CredentialPatterns.RedactCredentials(once);

        Assert.Equal(once, twice);
    }

    /// <summary>
    /// arb-ofz6: the <c>NamedCredential</c> arm must run on the non-backtracking engine, which is
    /// what closes its quadratic scan against a long separator-less run with enough character
    /// variety (near-keyword text is one instance of that shape). Structural, not timing-based:
    /// asserts the flag on the compiled arm's <see cref="Regex.Options"/> rather than measuring how
    /// long anything takes.
    /// </summary>
    [Fact]
    public void NamedCredential_arm_runs_on_the_nonbacktracking_engine()
    {
        var method = typeof(CredentialPatterns).GetMethod(
            "NamedCredential",
            BindingFlags.NonPublic | BindingFlags.Static,
            binder: null,
            types: Type.EmptyTypes,
            modifiers: null);

        Assert.NotNull(method);
        var regex = (Regex)method!.Invoke(null, null)!;

        Assert.True(
            regex.Options.HasFlag(RegexOptions.NonBacktracking),
            "NamedCredential must run on RegexOptions.NonBacktracking so a long separator-less run " +
            "seeded with near-keyword text cannot make its prefix scan quadratic.");
    }

    /// <summary>
    /// arb-ofz6: adversarial inputs that reach <c>NamedCredential</c>'s match timeout on the
    /// backtracking engine — a long separator-less <c>[\w-]</c> run with enough character variety.
    /// The corpus below covers both a near-keyword instance of that shape (<c>apikey</c> repeated)
    /// and a keyword-free instance (a cycled run of <c>a-z0-9_-</c>), per the review fixup on this
    /// bead: the trigger is the run's variety, not specifically that it looks like a keyword. A
    /// plain separator-less run of ONE repeated non-keyword character (e.g. <c>aaaa…</c>) does NOT
    /// trigger the blowup — measured outside this repo, it returns in ~1 ms on the unmodified
    /// backtracking engine. Measured in a throwaway project outside this repo (deleted after, never
    /// committed): run through a copy of the unmodified (backtracking) NamedCredential pattern, both
    /// rows below throw <see cref="RegexMatchTimeoutException"/> within the arm's match timeout (the
    /// keyword-free cycled row was reproduced there before being written down here). Through the
    /// real, non-backtracking arm exercised here, both must return unchanged without throwing.
    ///
    /// <para><b>What the positive control actually proves.</b> Appending a real credential
    /// assignment (<c>=PLACEHOLDERVALUE1234</c>) to the adversarial run and asserting it IS redacted
    /// does NOT prove the pathological shape itself was exercised: measured outside this repo, that
    /// combined input returns in 0 ms even on the unmodified backtracking engine, because the
    /// <c>=</c> separator gives the prefix scan an anchor and collapses what would otherwise be a
    /// quadratic search. What the combined-input assertion proves is only that the arm is reachable
    /// and still finds a credential appended after a long run of this shape — it is the FIRST
    /// assertion in each fact below (the adversarial run alone, unchanged, without throwing) that
    /// exercises and closes the pathological case; the combined-input row is not, and is not claimed
    /// to be, a timing-relevant control.</para>
    /// </summary>
    [Fact]
    public void A_long_separator_less_keyword_seeded_run_is_left_intact_without_timing_out()
    {
        var adversarialRun = string.Concat(Enumerable.Repeat("apikey", 16000 / "apikey".Length));

        var redacted = CredentialPatterns.RedactCredentials(adversarialRun);

        Assert.Equal(adversarialRun, redacted);

        // The same run, now followed by a real assignment, IS redacted — proving the arm is
        // reachable and still finds a credential following input of this shape. This does NOT
        // exercise the pathological case itself: measured outside this repo, appending "=<value>"
        // makes even the unmodified backtracking engine return in 0 ms, because the separator gives
        // the prefix scan an anchor. The assertion above (unchanged, no throw) is what proves the
        // pathological shape was exercised and closed.
        const string plantedSecret = "PLACEHOLDERVALUE1234";
        var withCredential = adversarialRun + "=" + plantedSecret;

        var redactedWithCredential = CredentialPatterns.RedactCredentials(withCredential);

        Assert.Contains(CredentialPatterns.Replacement, redactedWithCredential, StringComparison.Ordinal);
        Assert.DoesNotContain(plantedSecret, redactedWithCredential, StringComparison.Ordinal);
    }

    /// <summary>
    /// arb-ofz6: a second adversarial shape with NO keyword in it at all — cycling through
    /// <c>a-z0-9_-</c> reaches the same match timeout on the backtracking engine as the
    /// keyword-seeded run above, because the quadratic prefix scan is driven by the run's character
    /// variety, not by it resembling a credential name. Measured outside this repo before being
    /// written down here: a copy of the unmodified (backtracking) NamedCredential pattern throws
    /// <see cref="RegexMatchTimeoutException"/> on this exact input within the arm's match timeout.
    /// See the class-level remarks on <see cref="A_long_separator_less_keyword_seeded_run_is_left_intact_without_timing_out"/>
    /// for why the positive control below is reachability evidence, not a claim that it exercises
    /// this pathological shape.
    /// </summary>
    [Fact]
    public void A_long_separator_less_keyword_free_run_is_left_intact_without_timing_out()
    {
        const string alphabet = "abcdefghijklmnopqrstuvwxyz0123456789_-";
        var builder = new System.Text.StringBuilder();
        while (builder.Length < 16000)
        {
            builder.Append(alphabet);
        }

        var adversarialRun = builder.ToString(0, 16000);

        var redacted = CredentialPatterns.RedactCredentials(adversarialRun);

        Assert.Equal(adversarialRun, redacted);

        // Reachability control only — see the remarks above for why this does not itself exercise
        // the pathological case. This run has no keyword in it at all, so the credential-shaped
        // name has to be appended, not just a bare separator: "apikey=<value>" after the run.
        const string plantedSecret = "PLACEHOLDERVALUE5678";
        var withCredential = adversarialRun + "apikey=" + plantedSecret;

        var redactedWithCredential = CredentialPatterns.RedactCredentials(withCredential);

        Assert.Contains(CredentialPatterns.Replacement, redactedWithCredential, StringComparison.Ordinal);
        Assert.DoesNotContain(plantedSecret, redactedWithCredential, StringComparison.Ordinal);
    }

    /// <summary>
    /// arb-ofz6: prefix-semantics rows an atomic-group or bounded-prefix "optimisation" would break,
    /// per the rejected-alternatives note on the <c>NamedCredential</c> arm.
    ///
    /// <para>The first three rows plant a credential-shaped name that has MORE name characters after
    /// the keyword before the separator: an atomic group commits to the greedy match of
    /// <c>[\w-]*</c> as soon as it finds one and cannot backtrack to let the keyword alternation
    /// match later in the run, so it fails all three (measured outside this repo). A bounded prefix
    /// (e.g. <c>[\w-]{0,8}</c>) does NOT fail these three, though, because <c>\b</c> lets the engine
    /// restart the match at the hyphen closer to the keyword — measured outside this repo, all three
    /// still redact under an <c>{0,8}</c> bound. They still belong here as atomic-group regressions,
    /// but they do not by themselves prove a bounded prefix is safe.</para>
    ///
    /// <para>The fourth row, <c>vendorlongprefixapikey:</c>, has no hyphen or underscore between the
    /// excess prefix and the keyword for <c>\b</c> to restart from, so it discriminates a bounded
    /// prefix: measured outside this repo, an <c>[\w-]{0,8}</c> bound fails to match it at all
    /// (leaving the credential in the clear) while the shipped, non-backtracking pattern matches it
    /// correctly, identically to the unmodified backtracking pattern.</para>
    ///
    /// <para>The non-backtracking engine explores the same set of prefixes as the original
    /// backtracking pattern (it has no lookaround/backreference/atomic construct to diverge on), so
    /// it still finds all four.</para>
    /// </summary>
    [Theory]
    [InlineData("apikey_v2: PLACEHOLDERV2VALUE1", "PLACEHOLDERV2VALUE1")]
    [InlineData("x-secret-header-name: PLACEHOLDERHEADERVAL2", "PLACEHOLDERHEADERVAL2")]
    [InlineData("a-token-b-apikey-c: PLACEHOLDERCHAINVAL3", "PLACEHOLDERCHAINVAL3")]
    [InlineData("vendorlongprefixapikey: PLACEHOLDERLONGVENDOR9", "PLACEHOLDERLONGVENDOR9")]
    public void Prefix_semantics_survive_for_names_with_trailing_characters(string input, string secret)
    {
        // POSITIVE CONTROL: the planted value really is present before redaction.
        Assert.Contains(secret, input, StringComparison.Ordinal);

        var redacted = CredentialPatterns.RedactCredentials(input);

        Assert.Contains(CredentialPatterns.Replacement, redacted, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, redacted, StringComparison.Ordinal);
    }

    /// <summary>
    /// arb-01z (part 1 of arb-01z; parts 2 and 3 — moving the corpus to Arbitarr.TestSupport, and
    /// the ordered-list redesign for arb-qj9's ~7 new arms — are explicitly out of scope here; see
    /// the bd comment on arb-01z).
    ///
    /// <para><b>The gap this closes.</b> <c>CredentialPatternsCrossSinkTests</c> and every theory in
    /// this file are driven by <see cref="CredentialCorpus"/>. Nothing previously tied the ARM SET in
    /// <see cref="CredentialPatterns"/> to that corpus, so a new arm added without a matching corpus
    /// row would compile, run, and pass every existing test while being completely unexercised — the
    /// same "no test proves this fires" gap #206 flagged for arb-qj9's incoming ~7 arms.</para>
    ///
    /// <para><b>Why reflection over <c>[GeneratedRegex]</c>-decorated members, not a hand-kept name
    /// list.</b> A hand-kept list of arm names is exactly the "kept in step by a comment" pattern
    /// this whole type exists to remove (see the type's remarks on the two sinks drifting under
    /// arb-fbx). Discovering arms by attribute means a renamed or newly added
    /// <c>[GeneratedRegex]</c> arm is picked up automatically — nothing here needs editing when an
    /// arm's name changes, only when one is added with no corpus coverage, which is the failure this
    /// test exists to catch.</para>
    ///
    /// <para><b>Why match against the arm's OWN pattern, not against the aggregate
    /// <see cref="CredentialPatterns.RedactCredentials"/> output.</b> Running the whole corpus through
    /// <c>RedactCredentials</c> would let one arm's match hide another arm's total lack of coverage —
    /// exactly the failure mode of the vacuous "some row has it" assertions CLAUDE.md §4 warns
    /// against. Instead each arm's compiled <see cref="Regex"/> is invoked directly against every
    /// corpus input, so an arm is credited only by a match against ITS pattern, not the pipeline's
    /// combined effect.</para>
    ///
    /// <para><b>Positive control (CLAUDE.md §4), run outside this repo.</b> A throwaway console
    /// project holding a copy of the same four arms plus one extra dummy
    /// <c>[GeneratedRegex]</c> arm (a bare <c>ghp_[A-Za-z0-9]{20,}</c> GitHub-token shape) and no
    /// corresponding corpus row, driven through the same reflection-and-match logic as this test,
    /// reproduced the gap and printed:
    /// <c>FAIL: Arm "DummyGithubToken" is matched by no row in corpus.</c> and
    /// <c>1 out of 5 items in the collection did not pass.</c> Removing the dummy arm reduced it back
    /// to 0 of 4 failing — confirming the assertion is about ARM coverage, not merely corpus size. No
    /// code from that throwaway project is present in this repository.</para>
    /// </summary>
    [Fact]
    public void Every_arm_is_matched_by_at_least_one_corpus_row()
    {
        var arms = typeof(CredentialPatterns)
            .GetMethods(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.ReturnType == typeof(Regex) && m.GetParameters().Length == 0)
            .Where(m => m.GetCustomAttributes().Any(a => a.GetType().Name == "GeneratedRegexAttribute"))
            .ToList();

        // Sanity floor: if reflection stopped finding the arms (e.g. a signature shape it no longer
        // matches), an empty list would make Assert.All below pass vacuously over nothing.
        Assert.True(arms.Count >= 4, $"Expected at least 4 GeneratedRegex arms, found {arms.Count}.");

        var corpusInputs = CredentialCorpus().Select(row => (string)row[0]!).ToList();

        Assert.All(arms, method =>
        {
            var regex = (Regex)method.Invoke(null, null)!;
            var matchedByAnyRow = corpusInputs.Any(input => regex.IsMatch(input));

            Assert.True(
                matchedByAnyRow,
                $"Arm \"{method.Name}\" is matched by no row in {nameof(CredentialCorpus)} — " +
                "add a corpus row whose planted value has this arm's shape.");
        });
    }

    /// <summary>
    /// arb-0na2: the three shared arms that arb-ofz6 left out of scope, each pinned against an
    /// adversarial input derived from ITS OWN prefix shape rather than from
    /// <c>NamedCredential</c>'s.
    ///
    /// <para><b>These arms were MEASURED and do not blow up.</b> Outside this repo (a throwaway
    /// console project, deleted after, no code from it in this repository), each arm's pattern text
    /// was run against the adversarial shape for its own prefix from 2k to 32k characters. Every one
    /// stayed linear across that range, with times that merely DOUBLE as the input doubles, not
    /// quadratic. The same harness, in the same run, drove the pre-#546 backtracking
    /// <c>NamedCredential</c> pattern as a positive control and saw it grow roughly fivefold per
    /// doubling and then reach the match timeout, so the harness demonstrably CAN observe a blowup
    /// and the "completed" rows are not vacuous. No engine switch was therefore applied to these
    /// arms; they stay on the backtracking engine and these facts are regression pins, not fixes.</para>
    ///
    /// <para><b>Why each shape is the adversarial one for its arm.</b>
    /// <see cref="CredentialPatterns.RedactCredentials"/>'s remaining arms do NOT carry
    /// <c>NamedCredential</c>'s <c>\b[\w-]*</c> variable-length prefix beside the keyword — the shape
    /// that made that one quadratic. Each instead anchors its prefix on something fixed, so the way
    /// to stress it is to make that anchor fire as often as possible and fail as late as possible:
    /// a dense field of <c>?</c>/<c>&amp;</c> starts each followed by near-keyword text for the
    /// query-string arm; a dense field of <c>bearer</c>/<c>basic</c> near-misses at word boundaries
    /// for the scheme arm; and a dense field of bare credential nouns at word boundaries, each
    /// failing on the value floor, for the space-separated arm. Each arm is additionally driven with
    /// one keyword followed by an unterminated run of its own value class, the shape that would make
    /// a variable-length value adjacent to a variable-length prefix backtrack.</para>
    ///
    /// <para><b>What the assertions are.</b> Per CLAUDE.md §5 there is no wall-clock ceiling here:
    /// the pass condition is the arm's OWN <c>matchTimeoutMilliseconds</c>, which throws
    /// <see cref="RegexMatchTimeoutException"/> and fails the test if an arm ever regresses to
    /// quadratic on one of these shapes. Each fact then plants a real credential after the
    /// adversarial run and asserts <see cref="CredentialPatterns.Replacement"/> IS present and the
    /// planted value is gone — the positive control that proves the arm was reachable through input
    /// of this shape, rather than the absence assertion passing over a secret that never arrived.
    /// As on the arb-ofz6 facts above, that combined row is reachability evidence; it is the
    /// completion of the adversarial run itself, without throwing, that pins the scan.</para>
    ///
    /// <para>The fourth shared arm, <c>NamedCredential</c>, is pinned by the two arb-ofz6 facts
    /// above. The cleanser's own fifth arm, <c>WebhookUrl</c>, lives in <c>Arbitarr.Data</c> and is
    /// not reachable from this project; its redaction is pinned instead in
    /// <c>LogMessageCleanserTests.Redacts_a_webhook_url_whose_secret_is_in_the_path</c>. It was
    /// measured in the same out-of-repo harness (four shapes, including many <c>https://</c> starts
    /// each opening a fresh <c>[\w.-]*</c> scan, and an unterminated discord-like run) and is
    /// likewise linear from 2k to 32k characters; the adversarial long-input theory for this arm
    /// specifically was scoped out of arb-0na2 and is tracked as a follow-up.</para>
    /// </summary>
    [Theory]
    [InlineData("?api_ke&passwor?toke&passke", "?apikey=", "PLACEHOLDERVALUE2001")]
    [InlineData("bearer basi bearerbasi ", " bearer ", "PLACEHOLDERVALUE2002")]
    [InlineData("key token secret apikey passkey password ", " apikey ", "PLACEHOLDERVALUE2003")]
    public void An_adversarial_run_for_each_shared_arms_own_prefix_shape_completes_and_still_redacts(
        string adversarialUnit,
        string plantedPrefix,
        string plantedSecret)
    {
        const int adversarialLength = 16000;

        var builder = new System.Text.StringBuilder(adversarialLength + adversarialUnit.Length);
        while (builder.Length < adversarialLength)
        {
            builder.Append(adversarialUnit);
        }

        var adversarialRun = builder.ToString(0, adversarialLength);

        // The pass condition is the arm's own match timeout: if this arm ever regresses to a
        // quadratic scan on its own adversarial shape, RedactCredentials throws
        // RegexMatchTimeoutException here and the test fails. No wall-clock assertion.
        var redacted = CredentialPatterns.RedactCredentials(adversarialRun);

        Assert.NotNull(redacted);

        // Positive control: the same run followed by a real credential of this arm's shape. Proves
        // the arm is reachable through input of this shape, so the absence assertion below is not
        // passing over a secret that was never in play.
        var withCredential = adversarialRun + plantedPrefix + plantedSecret;
        Assert.Contains(plantedSecret, withCredential, StringComparison.Ordinal);

        var redactedWithCredential = CredentialPatterns.RedactCredentials(withCredential);

        Assert.Contains(CredentialPatterns.Replacement, redactedWithCredential, StringComparison.Ordinal);
        Assert.DoesNotContain(plantedSecret, redactedWithCredential, StringComparison.Ordinal);
    }

    /// <summary>
    /// arb-0na2: the second adversarial shape for each of the three arms — one keyword followed by
    /// an UNTERMINATED run of that arm's own value class. This is the shape that stresses a
    /// variable-length value sitting next to a variable-length prefix, which is the other half of
    /// the structure that made <c>NamedCredential</c> quadratic. Measured out of repo alongside the
    /// facts above: all three stay linear from 2k to 32k characters. Pass condition is again the
    /// arm's own match timeout, with the planted-credential positive control proving reachability.
    /// </summary>
    [Theory]
    [InlineData("?apikey=", "abcdefghijklmnopqrstuvwxyz0123456789._~-", "PLACEHOLDERVALUE3001")]
    [InlineData("bearer ", "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/=._~-", "PLACEHOLDERVALUE3002")]
    [InlineData("apikey ", "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_.+-", "PLACEHOLDERVALUE3003")]
    public void An_unterminated_value_class_run_after_each_arms_keyword_completes_and_still_redacts(
        string keyword,
        string valueAlphabet,
        string plantedSecret)
    {
        const int adversarialLength = 16000;

        var builder = new System.Text.StringBuilder(adversarialLength + valueAlphabet.Length);
        builder.Append(keyword);
        while (builder.Length < adversarialLength)
        {
            builder.Append(valueAlphabet);
        }

        var adversarialRun = builder.ToString(0, adversarialLength);

        // Pass condition: the arm's own match timeout. A regression to a quadratic scan throws here.
        var redacted = CredentialPatterns.RedactCredentials(adversarialRun);

        Assert.NotNull(redacted);

        // Positive control on a separate line of text, so the adversarial run's own unterminated
        // value cannot be what satisfies the assertion.
        var withCredential = adversarialRun + " " + keyword + plantedSecret;
        Assert.Contains(plantedSecret, withCredential, StringComparison.Ordinal);

        var redactedWithCredential = CredentialPatterns.RedactCredentials(withCredential);

        Assert.Contains(CredentialPatterns.Replacement, redactedWithCredential, StringComparison.Ordinal);
        Assert.DoesNotContain(plantedSecret, redactedWithCredential, StringComparison.Ordinal);
    }
}
