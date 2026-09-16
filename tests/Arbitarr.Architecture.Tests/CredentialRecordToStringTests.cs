using Mono.Cecil;
using Xunit;

namespace Arbitarr.Architecture.Tests;

/// <summary>
/// arb-1ox9: every record type that carries a raw secret must DECLARE its own <c>ToString</c>, or be
/// named in the exclusion table with the reason it deliberately does not.
///
/// <para><b>THE DEFECT.</b> A positional record's compiler-synthesised <c>ToString</c> prints every
/// member by name and value, so <c>SonarrCredential(Uri BaseUrl, string ApiKey)</c> renders as
/// <c>SonarrCredential { BaseUrl = …, ApiKey = the-actual-key }</c>. Any
/// <c>$"probe failed for {credential}"</c>, any <c>logger.LogWarning("… {Credential}", credential)</c>
/// emits the key verbatim.</para>
///
/// <para><b>WHAT THE EXISTING MECHANISMS COVER, MEASURED</b> (CLAUDE.md §1).
/// <c>IHttpClientFactory</c>'s URI redaction collapses an outbound request URI's QUERY STRING and
/// nothing else, so it covers no record rendering. <c>LogMessageCleanser</c> covers MORE than this
/// bead was filed assuming — its <c>NamedCredential</c> arm matches a credential-shaped name followed
/// by <c>:</c> or <c>=</c> anywhere in the text — but it covers it INCOMPLETELY and only in the log
/// sink. <c>PlaintextKey = …</c> matches no arm of it, so the two types carrying a live admin
/// credential were genuinely reaching the persistent store at <c>/api/admin/logs</c> (#65) verbatim;
/// <c>CredentialRecordLogInjectionTests</c> asserts that against the real pipeline. And nothing that
/// is not the sink — an exception message, console output — meets the cleanser at all.</para>
///
/// <para>Which is the argument for making this a SWEEP rather than a set of overrides: relying on a
/// denylist to recognise each new secret property's NAME is the arrangement that produced the
/// <c>PlaintextKey</c> gap. A rule that every secret-bearing record must render itself safely does
/// not have to know what the next one calls its field.</para>
///
/// <para><b>WHY THIS IS A TEST AND NOT THREE COMMENTS.</b> Several of the types this sweep covers
/// already carried a param doc saying "never logged" — and that doc stayed word-for-word correct
/// while the synthesised formatter printed the key anyway. A comment cannot fail; this can. It is the
/// same lesson <see cref="SecretReaderSingleCallerTests"/> records for the call-site count.</para>
///
/// <para><b>WHY CECIL, NOT SOURCE-GREP AND NOT REFLECTION.</b> A source regex is defeated by a
/// <c>{ }</c>-bodied record whose <c>ToString</c> sits fifty lines below the declaration, by an alias,
/// or by a partial. Reflection can SEE a <c>ToString</c> but cannot distinguish a DECLARED override
/// from the synthesised one, because both are real methods on the type — see
/// <see cref="DeclaresItsOwnToString"/> for the discriminator that can.</para>
///
/// <para><b>SCOPE: RECORDS ONLY, AND THAT IS A REAL LIMIT RATHER THAN AN OVERSIGHT.</b>
/// <see cref="IsRecord"/> skips every type that is not a record, so a secret-bearing PLAIN CLASS is
/// invisible to this sweep and nothing here asserts anything about how it renders. The limit is
/// deliberate because the DEFECT is record-specific: a plain class inherits
/// <c>object.ToString</c>, which prints only the type name and cannot leak a member, whereas a
/// record is handed a synthesised formatter that prints them all. A plain class only leaks once
/// somebody WRITES a <c>ToString</c> that leaks — a different defect, caught by a different check,
/// and one this sweep's "declares its own ToString" discriminator would actively mis-read as
/// compliance. A follow-up bead covers secret-bearing plain classes; do not widen
/// <see cref="IsRecord"/> here to chase them, because doing so would make the must-override
/// assertion demand an override from every class that correctly needs none.</para>
/// </summary>
public class CredentialRecordToStringTests
{
    /// <summary>
    /// The property names that make a record secret-bearing. Matched EXACTLY, from this named list —
    /// never <c>Contains("Key")</c>.
    ///
    /// <para><b>Why exact and not a substring match.</b> A "contains" match sweeps in
    /// <c>ApiKeyId</c>, <c>KeyPrefix</c>, <c>HashedKey</c>, <c>HasApiKey</c> and
    /// <c>ApiKeyResponse</c> — none of which is a raw secret — and a guard that fires on non-secrets
    /// gets widened until it fires on nothing. Several of those names are live in this codebase
    /// today: <c>ArrConfigResponse.HasApiKey</c> and <c>RadarrConfigResponse.HasApiKey</c> are
    /// BOOLEANS whose whole purpose is to report a key's presence WITHOUT carrying it, so a substring
    /// match would demand an override on the very types that solve this problem correctly.</para>
    /// </summary>
    /// <remarks>
    /// <b>THIS LIST MUST GROW WITH EVERY NEW SECRET SPELLING</b>, and that is the cost the exact
    /// match buys — it is the one part of this sweep that is still a denylist, and #346's review
    /// found two live gaps in it at once (<c>Key</c> and <c>WebhookUrl</c>; the first ended up in
    /// <see cref="SecretPropertyNamesOnCredentialShapedTypes"/> instead, for the measured reason
    /// recorded there). When adding a type that carries a raw secret under a name not listed here,
    /// add the NAME here in the same commit: the discovery test cannot flag what the scan cannot
    /// see. <c>Token</c>, <c>Passkey</c>,
    /// <c>ApiToken</c> and <c>ClientSecret</c> are deliberately ABSENT — no production record
    /// declares a member with any of those names today (checked, #346), and a name added before a
    /// member exists is an entry nothing proves the scan can match, which is the vacuity this file
    /// exists to avoid. Add each the day its member does.
    /// </remarks>
    private static readonly string[] SecretPropertyNames =
    [
        "ApiKey",
        "PlaintextKey",
        "PlaintextToken",
        "Password",
        "CurrentPassword",
        "NewPassword",
        "Secret",
        // #346 (security review): a webhook URL IS the credential — its token lives in the URL PATH,
        // which neither the query-string URI redaction nor any cleanser arm covers (CLAUDE.md §1).
        "WebhookUrl",
    ];

    /// <summary>
    /// Secret property names that count ONLY on a type the name arms already flag as
    /// credential-shaped (<see cref="IsCredentialShapedName"/>).
    ///
    /// <para><b>Why <c>Key</c> could not go in <see cref="SecretPropertyNames"/>, MEASURED.</b> #346
    /// added it there first, and the sweep immediately demanded a redacting <c>ToString</c> from
    /// three types that carry no secret whatever: <c>Arbitarr.Api.Dashboard.HealthItem</c> (a
    /// health-check identifier), <c>Arbitarr.Api.Admin.SettingCatalogEntryResponse</c>, and
    /// <c>Arbitarr.Core.Settings.SettingCatalogEntry</c> — whose <c>Key</c> is a
    /// <c>SettingKey</c> ENUM MEMBER, about as far from a raw secret as a member gets. That is
    /// exactly the over-firing <see cref="SecretPropertyNames"/>'s own comment warns about: a guard
    /// that fires on non-secrets gets widened until it fires on nothing. <c>Key</c> is simply too
    /// ordinary an identifier name in this codebase to mean "secret" unqualified.</para>
    ///
    /// <para><b>Why it is not dropped either.</b> The brief for #346 asked for both spellings closed
    /// so neither escapes, and the conjunction is what makes that safe rather than noisy: a record
    /// NAMED like a credential AND carrying a member called <c>Key</c> is a credential, while the
    /// same member on <c>HealthItem</c> is not. The type-name arm alone would already discover
    /// <c>NamedClientApiKey</c>; this arm is what discovers the NEXT such type if it is named
    /// <c>…Credential</c> and spells its member <c>Key</c>, which no current arm would catch.</para>
    /// </summary>
    private static readonly string[] SecretPropertyNamesOnCredentialShapedTypes =
    [
        "Key",
    ];

    /// <summary>
    /// Whether a type's NAME marks it as credential-shaped: it ends in <c>Credential</c> or in
    /// <c>ApiKey</c>.
    ///
    /// <para>Two suffixes, not one. #346: <c>NamedClientApiKey</c> escaped the original sweep on both
    /// available arms at once — its member is spelled <c>Key</c> (absent from
    /// <see cref="SecretPropertyNames"/> then) and its name ends in <c>ApiKey</c> rather than
    /// <c>Credential</c>. Widening only one arm would have closed that one type while leaving the
    /// next <c>…ApiKey</c>-named record with a differently-spelled member in the same blind spot.</para>
    /// </summary>
    private static bool IsCredentialShapedName(string typeName) =>
        typeName.EndsWith("Credential", StringComparison.Ordinal)
        || typeName.EndsWith("ApiKey", StringComparison.Ordinal);

    /// <summary>
    /// The assemblies scanned for secret-bearing records: ALL of them
    /// (<see cref="BuiltAssemblies.ProductionAssemblyNames"/>), rather than a hand-picked subset.
    ///
    /// <para>Arbitarr.Data and Arbitarr.Api hold eleven of the sixteen, and scanning only those two
    /// was the narrower option. It was rejected because the other five — the source/provider options
    /// records in Arbitarr.Media, Arbitarr.Sources.Newznab and Arbitarr.Sources.NzbHydra, and the
    /// startup seed record in Arbitarr.Host — carry a raw <c>ApiKey</c> with exactly the same hazard,
    /// and a scan that cannot see them would report a clean sweep while five types went uncovered.
    /// Taking the shared list wholesale also means a NEW production assembly is covered the day it is
    /// added to <see cref="BuiltAssemblies.ProductionAssemblyNames"/>, rather than the day somebody
    /// remembers to widen a subset here.</para>
    ///
    /// <para>Arbitarr.Host is scanned even though this project cannot REFERENCE it (NU1605) — the
    /// scan opens build output by path, which is exactly why
    /// <see cref="BuiltAssemblies.ResolveAssemblyPath"/> exists.</para>
    /// </summary>
    private static string[] ScannedAssemblies => BuiltAssemblies.ProductionAssemblyNames;

    /// <summary>
    /// Every secret-bearing record that MUST declare its own <c>ToString</c>, with what makes it a
    /// credential and what its override renders instead.
    ///
    /// <para><b>Spelled out, never globbed</b> — the reason
    /// <see cref="BuiltAssemblies.TestAssemblyNames"/> gives in general form: a list discovered from
    /// disk silently skips what was not built, and a silently skipped entry is a vacuous pass. Each
    /// entry is proven to be FOUND before any override is asserted.</para>
    ///
    /// <para><b>This table is deliberately SEPARATE from
    /// <see cref="RecordsThatMustNotOverrideToString"/>.</b> A single "known types" list that meant
    /// both "must override" and "must not" would be one edit away from flipping a type's posture
    /// unnoticed. Two tables make the flip a deliberate move between them, and
    /// <see cref="The_two_tables_are_disjoint"/> makes a type in both a failure rather than a silent
    /// contest between contradictory assertions.</para>
    /// </summary>
    private static readonly (string Assembly, string TypeName, string Why)[] RecordsThatMustOverrideToString =
    [
        // The three credential-provider outputs. Each is handed to code about to make a network
        // request — the code most likely to reach a log line — and each renders its address in full
        // with the key as CredentialPatterns.Replacement.
        ("Arbitarr.Data", "Arbitarr.Data.Media.SonarrCredential",
            "carries the Sonarr instance's raw ApiKey (arb-1ox9)"),
        ("Arbitarr.Data", "Arbitarr.Data.Media.RadarrCredential",
            "carries the Radarr instance's raw ApiKey (#318)"),
        ("Arbitarr.Data", "Arbitarr.Data.Sources.SourceCredential",
            "carries a per-source raw ApiKey (#319 / arb-x7w8.3)"),

        // The one-shot secret carriers. These print the type name and a NON-SECRET member (the row's
        // id and label) and no form of the key at all — not even a redaction marker, because there is
        // no non-secret rendering of a plaintext key to give.
        ("Arbitarr.Data", "Arbitarr.Data.Security.CreatedApiKey",
            "carries a live admin key's PlaintextKey, returned once and never stored"),
        ("Arbitarr.Data", "Arbitarr.Data.Security.IssuedSession",
            "carries a live session's PlaintextToken, written to one cookie and never stored"),
        ("Arbitarr.Api", "Arbitarr.Api.Admin.CreatedApiKeyResponse",
            "the only response in the codebase that carries a live admin credential"),

        // Admin request bodies carrying an operator-submitted key. The request-handling path is where
        // a "what did the client send us?" diagnostic is most likely to be added.
        ("Arbitarr.Api", "Arbitarr.Api.Admin.UpdateArrConfigRequest",
            "request body carrying a submitted Sonarr ApiKey"),
        ("Arbitarr.Api", "Arbitarr.Api.Admin.UpdateRadarrConfigRequest",
            "request body carrying a submitted Radarr ApiKey"),
        ("Arbitarr.Api", "Arbitarr.Api.Admin.CreateSourceRequest",
            "request body carrying a submitted per-source ApiKey"),
        ("Arbitarr.Api", "Arbitarr.Api.Admin.UpdateSourceRequest",
            "request body carrying a submitted per-source ApiKey"),
        // #346 (security review). The webhook URL is the only secret in this codebase whose value
        // lives in a URL PATH, so it is covered by neither the query-string URI redaction nor any
        // cleanser arm; its override renders the thresholds and redacts the URL outright.
        ("Arbitarr.Api", "Arbitarr.Api.Admin.UpdateNotificationConfigRequest",
            "request body carrying the #57 webhook URL, which IS the credential"),

        // Options and seed records holding a key for the lifetime of a provider or of startup. Each
        // of these already carried a param doc saying the key is "never logged" — a comment that
        // stayed correct-looking while the synthesised formatter printed it anyway. These overrides
        // are what turn that sentence into a mechanism.
        ("Arbitarr.Media", "Arbitarr.Media.Providers.ArrApiProviderOptions",
            "holds an *arr instance ApiKey for the provider's lifetime"),
        ("Arbitarr.Sources.Newznab", "Arbitarr.Sources.Newznab.NewznabSourceOptions",
            "holds a direct indexer's ApiKey for the source's lifetime"),
        ("Arbitarr.Sources.NzbHydra", "Arbitarr.Sources.NzbHydra.NzbHydraSourceOptions",
            "holds the NZBHydra2 ApiKey for the source's lifetime"),
        ("Arbitarr.Host", "Arbitarr.Host.Sources.EnvironmentSourceConfiguration",
            "holds the environment-supplied ApiKey read once at startup for divergence comparison"),

        // #346 (code review). This one is why BOTH discovery arms were widened in that commit: it
        // escaped the original sweep twice over — its member is spelled "Key", which was not in
        // SecretPropertyNames, and its type name ends in "ApiKey" rather than "Credential". A sweep
        // that finds every credential record except the one literally named for a key is the
        // vacuous-pass shape this whole file is written against.
        ("Arbitarr.Host", "Arbitarr.Host.Security.NamedClientApiKey",
            "holds a literal client API key bound from Arbitarr:ClientApiKeys at startup (#346)"),
    ];

    /// <summary>
    /// The secret-bearing records that MUST NOT declare a <c>ToString</c> — a deliberate, documented,
    /// DIFFERENT posture from the table above, not an oversight.
    ///
    /// <para><b>Why "no override" is the safer answer for exactly these two.</b> Both are password
    /// request bodies, and NEITHER HAS A NON-SECRET MEMBER: every field is a password. A redacting
    /// render would therefore say nothing useful while making the type LOOK safe to log, which is the
    /// opposite of what is wanted — they are defended by nothing ever logging them, and the record
    /// staying unremarkable is half of why. <c>PasswordLogInjectionTests</c> is what fails if that
    /// changes. The types in the other table all have an address, a row id or a label worth printing,
    /// which is what makes a safe render exist there and not here.</para>
    ///
    /// <para><b>Do not "fix" a failure here by moving the type to the other table.</b> The comment on
    /// <c>Arbitarr.Api.Security.ChangePasswordRequest</c> is the authority; this entry points at it.
    /// This table has its own non-vacuity control
    /// (<see cref="The_exclusion_table_is_not_vacuous"/>) because an exclusion the scan can no longer
    /// see is indistinguishable from the scan having stopped working.</para>
    /// </summary>
    private static readonly (string Assembly, string TypeName, string DefendingTest, string Why)[]
        RecordsThatMustNotOverrideToString =
    [
        ("Arbitarr.Api", "Arbitarr.Api.Security.ChangePasswordRequest",
            "tests/Arbitarr.Integration.Tests/PasswordLogInjectionTests.cs",
            "#96: every member is a password, so there is nothing safe to render; the type's own "
            + "comment says NO ToString OVERRIDE, AND DO NOT ADD ONE"),
        ("Arbitarr.Api", "Arbitarr.Api.Security.CredentialsRequest",
            "tests/Arbitarr.Integration.Tests/PasswordLogInjectionTests.cs",
            "#44: the login/setup sibling of ChangePasswordRequest, with the same posture and for "
            + "the same reason"),
    ];

    /// <summary>
    /// <b>POSITIVE CONTROL / NON-VACUITY, AND IT RUNS BEFORE ANY OVERRIDE IS ASSERTED.</b>
    ///
    /// <para>"Every credential record overrides ToString" passes perfectly against an EMPTY SET — a
    /// scan that has stopped matching anything, a project that was not built, a renamed namespace.
    /// <see cref="SecretReaderSingleCallerTests"/> documents this shape in its own comment and
    /// CLAUDE.md §4 records it as having shipped vacuous three times. So each named type must be
    /// FOUND by the scan first; if this test is red, every assertion in this file is vacuous and the
    /// scan is the thing to fix, not the types.</para>
    /// </summary>
    [Fact]
    public void The_scan_finds_every_record_the_tables_name()
    {
        var found = ScanForSecretBearingRecords();

        foreach (var (assembly, typeName, why) in RecordsThatMustOverrideToString)
        {
            Assert.True(
                found.Any(r => r.Assembly == assembly && r.TypeName == typeName),
                $"The scan found no secret-bearing record '{typeName}' in {assembly}, which is "
                + $"supposed to be one ({why}). Either the type moved or was renamed — update the "
                + "table — or the scan is broken, in which case EVERY assertion in this file is "
                + "vacuous, including the ones that are currently green.");
        }

        foreach (var (assembly, typeName, _, why) in RecordsThatMustNotOverrideToString)
        {
            Assert.True(
                found.Any(r => r.Assembly == assembly && r.TypeName == typeName),
                $"The scan found no secret-bearing record '{typeName}' in {assembly}, which is "
                + $"supposed to be one ({why}). The exclusion below is meaningless until the scan "
                + "can see the type it excludes — an exclusion nothing matches is indistinguishable "
                + "from a scan that has stopped working.");
        }
    }

    /// <summary>
    /// Every record in the must-override table declares its own <c>ToString</c>. Meaningful only
    /// because <see cref="The_scan_finds_every_record_the_tables_name"/> has established the scan
    /// sees them.
    /// </summary>
    [Fact]
    public void Every_secret_bearing_record_in_the_table_declares_its_own_ToString()
    {
        var found = ScanForSecretBearingRecords();

        var missing = RecordsThatMustOverrideToString
            .Where(e => found.Any(r =>
                r.Assembly == e.Assembly && r.TypeName == e.TypeName && !r.DeclaresOwnToString))
            .Select(e => $"{e.TypeName} ({e.Why})")
            .ToArray();

        Assert.True(
            missing.Length == 0,
            "These records carry a raw secret and rely on the compiler-synthesised ToString, which "
            + "prints every member by name and value — so any interpolation of one emits the secret "
            + "verbatim into the persistent log store served at /api/admin/logs. Neither the "
            + "IHttpClientFactory URI redaction nor LogMessageCleanser covers it: both are scoped to "
            + "query strings and a record's string form is not a URI (CLAUDE.md §1). Add an override "
            + "that renders the non-secret members and replaces the secret with "
            + "CredentialPatterns.Replacement — never a literal '<redacted>', which would be a second "
            + "spelling a search has to know about separately. Found:\n  "
            + string.Join("\n  ", missing));
    }

    /// <summary>
    /// The excluded records still declare NO <c>ToString</c> of their own.
    ///
    /// <para>This is the assertion that makes the exclusion table load-bearing rather than
    /// decorative: without it, adding an override to <c>ChangePasswordRequest</c> — the change its
    /// own comment forbids — would pass every test in this file.</para>
    /// </summary>
    [Fact]
    public void The_excluded_records_still_declare_no_ToString()
    {
        var found = ScanForSecretBearingRecords();

        var unexpected = RecordsThatMustNotOverrideToString
            .Where(e => found.Any(r =>
                r.Assembly == e.Assembly && r.TypeName == e.TypeName && r.DeclaresOwnToString))
            .Select(e => $"{e.TypeName} (defended by {e.DefendingTest}: {e.Why})")
            .ToArray();

        Assert.True(
            unexpected.Length == 0,
            "These records are documented as deliberately having NO ToString override, and now "
            + "declare one. This is not the same defect as a missing override — it is the opposite "
            + "posture, and it was chosen because every member of these types is a password, so a "
            + "redacting render says nothing while making the type look safe to log. Read the "
            + "type's own comment before changing this. Found:\n  "
            + string.Join("\n  ", unexpected));
    }

    /// <summary>
    /// <b>DISCOVERY — the part that makes this sweep worth having.</b> Without it the tables are
    /// fourteen hand-written assertions and a fifteenth secret-bearing record added next month is
    /// covered by nothing.
    ///
    /// <para>Every secret-bearing record the scan finds must be in EXACTLY ONE table. A type in
    /// neither is the gap this whole file exists to close, and the failure message says which choice
    /// the author has to make rather than leaving them to guess.</para>
    /// </summary>
    [Fact]
    public void Every_secret_bearing_record_found_is_accounted_for_in_exactly_one_table()
    {
        var found = ScanForSecretBearingRecords();

        // The premise: the scan sees something at all. Without this the "no unaccounted records"
        // assertion below is satisfied just as happily by a scan that matches nothing.
        Assert.NotEmpty(found);

        var unaccounted = found
            .Where(r => !RecordsThatMustOverrideToString
                    .Any(e => e.Assembly == r.Assembly && e.TypeName == r.TypeName)
                && !RecordsThatMustNotOverrideToString
                    .Any(e => e.Assembly == r.Assembly && e.TypeName == r.TypeName))
            .Select(r => $"{r.TypeName} ({r.Assembly}; matched on: {r.MatchReason})")
            .ToArray();

        Assert.True(
            unaccounted.Length == 0,
            "These records carry a raw secret and are in NEITHER table, so nothing asserts anything "
            + "about how they render. Decide which posture applies and add the entry:\n"
            + "  - RecordsThatMustOverrideToString, if the type has a non-secret member worth "
            + "printing (an address, a row id, a label). Add the override too.\n"
            + "  - RecordsThatMustNotOverrideToString, if every member is a secret, so a redacting "
            + "render would say nothing while making the type look safe to log — the posture "
            + "ChangePasswordRequest documents. Name the test that defends it.\n"
            + "Never both, and never neither. Found:\n  " + string.Join("\n  ", unaccounted));
    }

    /// <summary>
    /// The two tables are DISJOINT. A type in both would make this file assert contradictory things
    /// about it, and whichever assertion ran second would silently lose — the failure mode the
    /// two-table split exists to prevent, and the one that is invisible while both tables happen to
    /// agree.
    /// </summary>
    [Fact]
    public void The_two_tables_are_disjoint()
    {
        var overlap = RecordsThatMustOverrideToString
            .Where(m => RecordsThatMustNotOverrideToString
                .Any(x => x.Assembly == m.Assembly && x.TypeName == m.TypeName))
            .Select(m => m.TypeName)
            .ToArray();

        Assert.True(
            overlap.Length == 0,
            "These types are in BOTH tables, so this file demands that they simultaneously do and do "
            + "not declare a ToString. Remove the entry from whichever table does not match the "
            + "type's documented posture:\n  " + string.Join("\n  ", overlap));
    }

    /// <summary>
    /// <b>NON-VACUITY FOR THE EXCLUSION TABLE SPECIFICALLY.</b> Held separately from
    /// <see cref="The_scan_finds_every_record_the_tables_name"/> so that an exclusion going stale
    /// fails with a message about the EXCLUSION rather than about the sweep in general — and so that
    /// deleting the must-override table could never leave the exclusion's control passing on the
    /// strength of the other table's entries.
    ///
    /// <para>Asserted per entry, and on <c>ChangePasswordRequest</c> by name, because that is the one
    /// the sweep is most likely to be "fixed" by removing.</para>
    /// </summary>
    [Fact]
    public void The_exclusion_table_is_not_vacuous()
    {
        Assert.NotEmpty(RecordsThatMustNotOverrideToString);

        // THE EXCLUSION TABLE'S SIZE IS PINNED (#346 review). "Not empty" would stay green while the
        // table quietly grew, and every entry added to it is a type this file stops requiring a
        // ToString override from — a POSTURE DECISION, never a way to make a red sweep go green.
        // Growing it needs the lead's ruling and the type's own comment documenting why it has no
        // non-secret member worth rendering; only then update this number. Shrinking it is likewise
        // a decision: it means a type moved to the must-override table, not that an entry was tidied.
        Assert.Equal(2, RecordsThatMustNotOverrideToString.Length);

        var found = ScanForSecretBearingRecords();

        Assert.Contains(
            RecordsThatMustNotOverrideToString,
            e => e.TypeName == "Arbitarr.Api.Security.ChangePasswordRequest");

        foreach (var (assembly, typeName, defendingTest, _) in RecordsThatMustNotOverrideToString)
        {
            Assert.True(
                found.Any(r => r.Assembly == assembly && r.TypeName == typeName),
                $"'{typeName}' is excluded from the override requirement, but the scan can no "
                + $"longer see it in {assembly} as a secret-bearing record. A stale exclusion is an "
                + "exclusion nobody is reviewing: either the type changed shape (and the entry "
                + $"should go), or the scan broke (and {defendingTest} is now the only thing "
                + "defending it).");
        }
    }

    /// <summary>
    /// Proves the DISCRIMINATOR works — that <see cref="DeclaresItsOwnToString"/> can actually tell a
    /// hand-written override from the compiler-synthesised one, in BOTH directions, on types this
    /// scan really reads.
    ///
    /// <para>Without this, a discriminator that returned <c>true</c> for everything would leave
    /// <see cref="Every_secret_bearing_record_in_the_table_declares_its_own_ToString"/> green with
    /// every override deleted, and one that returned <c>false</c> for everything would be caught only
    /// by the exclusion test. Both directions are asserted because one alone is half a control.</para>
    ///
    /// <para>The witnesses are real types rather than a planted bait: <c>RadarrCredential</c> has a
    /// hand-written override (#318) and <c>ChangePasswordRequest</c> is documented as never having
    /// one, so they cannot be quietly changed to make this pass.</para>
    /// </summary>
    [Fact]
    public void The_discriminator_separates_a_declared_override_from_the_synthesised_one()
    {
        var found = ScanForSecretBearingRecords();

        var handWritten = found.Single(r => r.TypeName == "Arbitarr.Data.Media.RadarrCredential");
        Assert.True(
            handWritten.DeclaresOwnToString,
            "RadarrCredential has had a hand-written ToString override since #318, so a "
            + "discriminator that reports otherwise is broken — and would make every override "
            + "assertion in this file fail for the wrong reason.");

        var synthesised = found.Single(r => r.TypeName == "Arbitarr.Api.Security.ChangePasswordRequest");
        Assert.False(
            synthesised.DeclaresOwnToString,
            "ChangePasswordRequest is documented as deliberately having NO override, so a "
            + "discriminator that reports one is matching the compiler-synthesised method — which "
            + "would make every override assertion in this file pass vacuously.");
    }

    /// <summary>
    /// <b>POSITIVE CONTROL FOR THE TWO ARMS #346 ADDED</b>, asserted on the MATCH REASON rather than
    /// on mere membership.
    ///
    /// <para>Why the reason and not just "the scan found it": <c>NamedClientApiKey</c> would appear
    /// in <see cref="The_scan_finds_every_record_the_tables_name"/>'s results if EITHER new arm
    /// fired, so that test stays green with one of the two silently dead — and a dead arm is exactly
    /// what let this type escape the original sweep. Widening both was deliberate (neither spelling
    /// should escape), so both are proven to fire, each by the string the scan itself reports.</para>
    ///
    /// <para><b>Stated honestly: for THIS witness the two are not independent.</b> The scoped
    /// property arm only consults
    /// <see cref="SecretPropertyNamesOnCredentialShapedTypes"/> once the name arm has already matched,
    /// so <c>Key</c> cannot fire here without <c>ApiKey</c> having fired first. What the pair still
    /// proves is that the scoped arm is REACHED and matches rather than being dead code — which is
    /// what makes it catch the case it exists for: a future <c>…Credential</c>-named record whose
    /// member is spelled <c>Key</c>, where the name arm alone would report no property at all.</para>
    ///
    /// <para>Read together with
    /// <see cref="Every_secret_bearing_record_found_is_accounted_for_in_exactly_one_table"/>, which
    /// is what fails if a FUTURE type escapes every arm, and with
    /// <see cref="The_Key_name_does_not_sweep_in_types_whose_Key_is_an_identifier"/>, which is what
    /// fails if the scoping is removed and the arm starts over-firing instead.</para>
    /// </summary>
    [Fact]
    public void Both_arms_added_for_NamedClientApiKey_and_the_webhook_url_really_fire()
    {
        var found = ScanForSecretBearingRecords();

        var clientKey = found.Single(r => r.TypeName == "Arbitarr.Host.Security.NamedClientApiKey");

        // The SCOPED PROPERTY arm: "Key" counts here because the type name is credential-shaped.
        Assert.Contains("properties Key", clientKey.MatchReason, StringComparison.Ordinal);

        // The TYPE-NAME arm: the 'ApiKey' suffix matched independently of the property above. If
        // this half were removed, the assertion above would still pass while a future
        // '…ApiKey'-named record with a differently-spelled member went undiscovered.
        Assert.Contains("name ends in 'ApiKey'", clientKey.MatchReason, StringComparison.Ordinal);

        // And the WebhookUrl entry, whose whole discovery rests on that one name being in the list.
        var notificationRequest = found.Single(
            r => r.TypeName == "Arbitarr.Api.Admin.UpdateNotificationConfigRequest");
        Assert.Contains("properties WebhookUrl", notificationRequest.MatchReason, StringComparison.Ordinal);

        // Both are secret-bearing records that DO declare an override — the posture their table entry
        // demands, asserted here too so this control fails loudly if an override is deleted while
        // the arms still fire.
        Assert.True(clientKey.DeclaresOwnToString);
        Assert.True(notificationRequest.DeclaresOwnToString);
    }

    /// <summary>
    /// <b>NEGATIVE CONTROL FOR THE SCOPING OF <c>Key</c>, on the three types that MEASURED it.</b>
    ///
    /// <para>#346 first put <c>Key</c> in <see cref="SecretPropertyNames"/> unconditionally, and
    /// these three went from unmatched to demanding a redacting <c>ToString</c> — none of them
    /// carries a secret, and <c>SettingCatalogEntry.Key</c> is an ENUM member. Moving the name into
    /// <see cref="SecretPropertyNamesOnCredentialShapedTypes"/> is what fixed that, so this test
    /// pins the fix on the exact witnesses rather than on the principle.</para>
    ///
    /// <para>Without it, promoting <c>Key</c> back to the unconditional list would show up only as
    /// <see cref="Every_secret_bearing_record_found_is_accounted_for_in_exactly_one_table"/> failing
    /// with a message about missing table entries — which reads as "add three entries" rather than
    /// "the guard is over-firing", and adding them is the wrong fix: a guard that fires on
    /// non-secrets gets widened until it fires on nothing.</para>
    /// </summary>
    [Fact]
    public void The_Key_name_does_not_sweep_in_types_whose_Key_is_an_identifier()
    {
        var found = ScanForSecretBearingRecords();

        string[] identifierKeyTypes =
        [
            "Arbitarr.Api.Dashboard.HealthItem",
            "Arbitarr.Api.Admin.SettingCatalogEntryResponse",
            "Arbitarr.Core.Settings.SettingCatalogEntry",
        ];

        var sweptIn = found
            .Where(r => identifierKeyTypes.Contains(r.TypeName, StringComparer.Ordinal))
            .Select(r => $"{r.TypeName} (matched on: {r.MatchReason})")
            .ToArray();

        Assert.True(
            sweptIn.Length == 0,
            "The scan now treats these as secret-bearing, but their 'Key' is an IDENTIFIER — a "
            + "health-check id and, for the catalog entries, a SettingKey enum member. This is what "
            + "happens when 'Key' is matched unconditionally instead of only on a "
            + "credential-shaped type name (SecretPropertyNamesOnCredentialShapedTypes). Do not "
            + "resolve it by adding table entries for them: a guard that fires on non-secrets gets "
            + "widened until it fires on nothing. Restore the scoping. Found:\n  "
            + string.Join("\n  ", sweptIn));
    }

    /// <summary>
    /// Whether <paramref name="type"/> declares its OWN <c>ToString</c>, as opposed to carrying the
    /// one the record synthesiser emitted.
    ///
    /// <para><b>THIS IS THE WHOLE DIFFICULTY, AND WHY REFLECTION CANNOT ANSWER IT.</b> A record ALWAYS
    /// has a <c>ToString</c> method defined on the type itself — the compiler emits one — so
    /// "does this type have a ToString?" is <c>true</c> for every record and answers nothing. The
    /// question is who WROTE it.</para>
    ///
    /// <para>The discriminator is <c>[CompilerGenerated]</c>: Roslyn marks the record's synthesised
    /// <c>ToString</c> with it, and a hand-written override in source has no such attribute. Verified
    /// against this codebase in both directions — <c>RadarrCredential</c>'s hand-written override
    /// carries no attributes while <c>ChangePasswordRequest</c>'s synthesised one carries
    /// <c>[CompilerGenerated]</c> — and
    /// <see cref="The_discriminator_separates_a_declared_override_from_the_synthesised_one"/> keeps
    /// that true rather than assuming it.</para>
    ///
    /// <para>A second signal corroborates it and is deliberately NOT used as the test: the synthesised
    /// body calls <c>PrintMembers</c>, which a hand-written one does not. It was rejected because it
    /// reads the METHOD BODY, so a hand-written override that legitimately chose to call
    /// <c>PrintMembers</c> for its non-secret members would be misreported as synthesised — a
    /// false pass on exactly the shape somebody might write next. The attribute is a property of who
    /// emitted the method, which is the actual question.</para>
    /// </summary>
    private static bool DeclaresItsOwnToString(TypeDefinition type) =>
        type.Methods.Any(m =>
            m.Name == "ToString"
            && !m.IsStatic
            && m.Parameters.Count == 0
            && m.ReturnType.FullName == "System.String"
            && !m.CustomAttributes.Any(a =>
                a.AttributeType.FullName
                    == "System.Runtime.CompilerServices.CompilerGeneratedAttribute"));

    /// <summary>
    /// Whether <paramref name="type"/> is a record.
    ///
    /// <para>Detected by the compiler-generated <c>&lt;Clone&gt;$</c> method, which the record
    /// synthesiser emits for every record and which is not expressible in C# source — so no ordinary
    /// class can accidentally look like one. The alternative signal, an <c>EqualityContract</c>
    /// property, was rejected because it is an ordinary property name a hand-written class could
    /// declare, and because a record STRUCT has no <c>EqualityContract</c> at all while this check
    /// should keep working if one ever carries a secret.</para>
    /// </summary>
    private static bool IsRecord(TypeDefinition type) =>
        type.Methods.Any(m => m.Name == "<Clone>$");

    /// <summary>
    /// Every secret-bearing record across <see cref="ScannedAssemblies"/>.
    ///
    /// <para>A record is secret-bearing when <see cref="IsCredentialShapedName"/> matches its name,
    /// OR it declares an instance property whose name is exactly one of
    /// <see cref="SecretPropertyNames"/>, OR — on a credential-shaped name only — one of
    /// <see cref="SecretPropertyNamesOnCredentialShapedTypes"/>. The name-suffix arms catch a
    /// credential type whose member is called something the lists do not know; the property arm
    /// catches a secret-carrying type that is not named like one, which is most of them; the scoped
    /// arm catches a name too ordinary to mean "secret" on its own.</para>
    /// </summary>
    private static List<(string Assembly, string TypeName, bool DeclaresOwnToString, string MatchReason)>
        ScanForSecretBearingRecords()
    {
        var results = new List<(string, string, bool, string)>();

        foreach (var assemblyName in ScannedAssemblies)
        {
            var path = BuiltAssemblies.ResolveAssemblyPath("src", assemblyName);

            // Loud failure, not a vacuous pass: an assembly the scan cannot open is not an assembly
            // it has found to be clean (CLAUDE.md §4, and BuiltAssemblies' own contract).
            Assert.True(
                path is not null,
                $"Could not locate the built assembly for {assemblyName}. This scan reads IL from "
                + "the project's build output, so the SOLUTION must be built before "
                + "Arbitarr.Architecture.Tests runs. Run 'dotnet build' at the solution level "
                + "first; in CI the job must build the solution before running this project. "
                + "Failing here rather than skipping is deliberate — a skipped assembly would make "
                + "every assertion in this file vacuous for the types it holds.");

            using var module = ModuleDefinition.ReadModule(path!);

            foreach (var type in module.GetTypes())
            {
                if (!IsRecord(type))
                {
                    continue;
                }

                var namedCredential = IsCredentialShapedName(type.Name);

                // The unconditional names, plus the credential-shape-scoped ones (#346). Scoping the
                // second set is what lets "Key" count on NamedClientApiKey without demanding an
                // override from HealthItem or SettingCatalogEntry — see the field's own comment.
                var secretProperties = type.Properties
                    .Where(p => p.HasThis
                        && (SecretPropertyNames.Contains(p.Name, StringComparer.Ordinal)
                            || (namedCredential
                                && SecretPropertyNamesOnCredentialShapedTypes.Contains(
                                    p.Name, StringComparer.Ordinal))))
                    .Select(p => p.Name)
                    .ToArray();

                if (!namedCredential && secretProperties.Length == 0)
                {
                    continue;
                }

                var nameSuffix = type.Name.EndsWith("Credential", StringComparison.Ordinal)
                    ? "Credential"
                    : "ApiKey";
                var reason = namedCredential && secretProperties.Length > 0
                    ? $"name ends in '{nameSuffix}'; properties {string.Join(", ", secretProperties)}"
                    : namedCredential
                        ? $"name ends in '{nameSuffix}'"
                        : $"properties {string.Join(", ", secretProperties)}";

                results.Add((assemblyName, type.FullName, DeclaresItsOwnToString(type), reason));
            }
        }

        return results;
    }
}
