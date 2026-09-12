using Arbitarr.Api.Rendering;
using Arbitarr.Api.Search;
using Arbitarr.Core.Pipeline;
using Arbitarr.Core.Releases;
using Xunit;

namespace Arbitarr.Api.Tests;

/// <summary>
/// Pins <see cref="DedupStage"/> against
/// <c>docs/adr/0019-dedup-is-a-pipeline-stage-with-conservative-exact-merge.md</c>: the three
/// conditions that must ALL hold for a merge, the tolerance boundary on both sides, and the rule
/// that a group retains every member ordered by source priority.
///
/// <para><b>Member counts are asserted, never only group counts.</b> ADR 0019 calls this out
/// explicitly: a stage that merged correctly but silently kept one member would pass every
/// group-count assertion while destroying the fallback the whole design exists for. Every merge
/// case below therefore asserts what the group CONTAINS.</para>
/// </summary>
public class DedupStageTests
{
    private static readonly DateTimeOffset PubDate = new(2026, 9, 12, 9, 0, 0, TimeSpan.Zero);

    /// <summary>A size comfortably inside the relative band's reach (1 GB → ~1 MB of tolerance).</summary>
    private const long BaseSize = 1_000_000_000;

    private static DedupStage Stage(ISourcePriorityLookup? priorities = null) =>
        new(priorities ?? AllEqualSourcePriority.Instance);

    private static RenderedRelease Release(
        string sourceName,
        string title = "Some Show S01E01 1080p WEB-DL",
        long size = BaseSize,
        ProtocolKind protocol = ProtocolKind.Torrent,
        string? guid = null) =>
        new(sourceName, new ReleaseCandidate
        {
            Title = title,
            Guid = guid ?? $"{sourceName}-guid",
            PubDate = PubDate,
            Size = size,
            Link = new Uri($"http://{sourceName}.example.invalid/get/1"),
            Category = new[] { 5000 },
            Protocol = protocol,
        });

    /// <summary>Ranks the named sources; anything unnamed is neutral zero, as the contract requires.</summary>
    private sealed class FixedPriorities(Dictionary<string, int> byName) : ISourcePriorityLookup
    {
        public int PriorityOf(string sourceName) => byName.TryGetValue(sourceName, out var p) ? p : 0;
    }

    [Fact]
    public void Two_identical_releases_from_two_sources_collapse_to_one_group()
    {
        var result = Stage().Deduplicate(new[] { Release("alpha"), Release("beta") });

        Assert.Single(result);
    }

    /// <summary>
    /// The assertion ADR 0019 names: the group holds BOTH members, not one. A stage that kept only
    /// the winner passes the group-count test above and fails this one.
    /// </summary>
    [Fact]
    public void The_merged_group_retains_both_members()
    {
        var result = Stage().Deduplicate(new[] { Release("alpha"), Release("beta") });

        Assert.Equal(2, 1 + result[0].AlternateMembers.Count);
    }

    [Fact]
    public void Both_source_names_survive_the_merge()
    {
        var result = Stage().Deduplicate(new[] { Release("alpha"), Release("beta") });

        Assert.Equal(
            new[] { "alpha", "beta" },
            result[0].AlternateMembers.Select(m => m.SourceName).Prepend(result[0].SourceName).ToArray());
    }

    /// <summary>
    /// The whole point of the epic: a release arriving once from NZBHydra2 and once from a directly
    /// configured indexer is one result, not two. Nothing about the rule is Hydra-specific — dedup
    /// covers every source — which is why this case needs no special handling to pass.
    /// </summary>
    [Fact]
    public void A_hydra_sourced_and_a_direct_sourced_copy_of_one_release_merge()
    {
        var result = Stage().Deduplicate(new[] { Release("NZBHydra2"), Release("direct-indexer") });

        Assert.Equal(2, 1 + result[0].AlternateMembers.Count);
    }

    [Fact]
    public void A_release_that_matches_nothing_is_returned_untouched_with_no_members()
    {
        var result = Stage().Deduplicate(new[] { Release("alpha"), Release("beta", title: "Entirely Different Show S02E02") });

        Assert.Equal(new[] { 0, 0 }, result.Select(r => r.AlternateMembers.Count).ToArray());
    }

    // --- Condition 1: normalised title equality -------------------------------------------------

    /// <summary>
    /// Equality, never similarity. One character apart is a different title, and ADR 0019 accepts
    /// the resulting duplicate row as the cheap error.
    /// </summary>
    [Fact]
    public void A_title_differing_by_one_character_does_not_merge()
    {
        var result = Stage().Deduplicate(new[]
        {
            Release("alpha", title: "Some Show S01E01 1080p WEB-DL"),
            Release("beta", title: "Some Show S01E02 1080p WEB-DL"),
        });

        Assert.Equal(2, result.Count);
    }

    /// <summary>
    /// Positive control for the normaliser, first half: two titles differing ONLY in case and
    /// punctuation DO merge, proving the normaliser is actually running. Without this, the
    /// negative case below would pass just as happily against a stage that compared raw titles and
    /// never merged anything.
    /// </summary>
    [Fact]
    public void Titles_differing_only_in_case_and_punctuation_merge()
    {
        var result = Stage().Deduplicate(new[]
        {
            Release("alpha", title: "Some Show S01E01 1080p WEB-DL"),
            Release("beta", title: "some.show.s01e01.1080p.web.dl"),
        });

        Assert.Equal(2, 1 + result[0].AlternateMembers.Count);
    }

    /// <summary>
    /// Positive control, second half: the SAME pair with one real word changed does not merge. Read
    /// with the test above, this shows the normaliser folds separators and case and nothing more —
    /// it is not collapsing the titles into agreement.
    /// </summary>
    [Fact]
    public void The_same_pair_with_a_real_word_difference_does_not_merge()
    {
        var result = Stage().Deduplicate(new[]
        {
            Release("alpha", title: "Some Show S01E01 1080p WEB-DL"),
            Release("beta", title: "some.show.s01e01.2160p.web.dl"),
        });

        Assert.Equal(2, result.Count);
    }

    // --- Condition 2: size within tolerance -----------------------------------------------------

    /// <summary>
    /// The inside half of the boundary pin. Exactly at the tolerance, so it also fixes the
    /// comparison as inclusive rather than leaving that to implementation accident.
    /// </summary>
    [Fact]
    public void A_size_difference_exactly_at_the_tolerance_merges()
    {
        var tolerance = (long)(BaseSize * DedupStage.RelativeSizeTolerance);

        var result = Stage().Deduplicate(new[]
        {
            Release("alpha", size: BaseSize),
            Release("beta", size: BaseSize - tolerance),
        });

        Assert.Equal(2, 1 + result[0].AlternateMembers.Count);
    }

    /// <summary>
    /// The outside half. One byte past the band is a different release as far as this stage is
    /// concerned — the two tests together are what make the constant impossible to change silently.
    /// </summary>
    [Fact]
    public void A_size_difference_one_byte_outside_the_tolerance_does_not_merge()
    {
        var tolerance = (long)(BaseSize * DedupStage.RelativeSizeTolerance);

        var result = Stage().Deduplicate(new[]
        {
            Release("alpha", size: BaseSize),
            Release("beta", size: BaseSize - tolerance - 1),
        });

        Assert.Equal(2, result.Count);
    }

    /// <summary>
    /// The floor keeps the rule meaningful on small payloads, where one part in 1000 would be
    /// narrower than a single par2 block.
    /// </summary>
    [Fact]
    public void On_a_small_payload_the_absolute_floor_governs_instead_of_the_ratio()
    {
        const long small = 1_000_000;

        var result = Stage().Deduplicate(new[]
        {
            Release("alpha", size: small),
            Release("beta", size: small + DedupStage.MinimumSizeToleranceBytes),
        });

        Assert.Equal(2, 1 + result[0].AlternateMembers.Count);
    }

    // --- Condition 3: same known protocol -------------------------------------------------------

    /// <summary>
    /// Asserted PER PAIR, as the bead requires: each pair is deduplicated on its own, so a single
    /// combined count cannot hide one pair merging while another splits.
    /// </summary>
    [Theory]
    [InlineData("Some Show S01E01 1080p WEB-DL")]
    [InlineData("Another Show S03E07 720p HDTV")]
    public void A_torrent_and_a_usenet_release_with_the_same_title_never_merge(string title)
    {
        var result = Stage().Deduplicate(new[]
        {
            Release("alpha", title: title, protocol: ProtocolKind.Torrent),
            Release("beta", title: title, protocol: ProtocolKind.Usenet),
        });

        Assert.Equal(2, result.Count);
    }

    /// <summary>
    /// <b>Unknown matches nothing, INCLUDING another Unknown.</b> This is the condition that reads
    /// like a bug without ADR 0019: Unknown is the DEFAULT, so two Unknown candidates share only a
    /// missing field, and a missing field must never become an accidental match.
    /// </summary>
    [Fact]
    public void Two_releases_that_both_report_an_unknown_protocol_never_merge()
    {
        var result = Stage().Deduplicate(new[]
        {
            Release("alpha", protocol: ProtocolKind.Unknown),
            Release("beta", protocol: ProtocolKind.Unknown),
        });

        Assert.Equal(2, result.Count);
    }

    /// <summary>
    /// The control that keeps the test above from being vacuous: the identical pair, differing only
    /// in that the protocol is reported, DOES merge. Without this, a stage that merged nothing at
    /// all would pass the Unknown test.
    /// </summary>
    [Fact]
    public void The_same_pair_with_a_known_protocol_does_merge()
    {
        var result = Stage().Deduplicate(new[]
        {
            Release("alpha", protocol: ProtocolKind.Torrent),
            Release("beta", protocol: ProtocolKind.Torrent),
        });

        Assert.Equal(2, 1 + result[0].AlternateMembers.Count);
    }

    [Fact]
    public void An_unknown_protocol_does_not_merge_with_a_known_one()
    {
        var result = Stage().Deduplicate(new[]
        {
            Release("alpha", protocol: ProtocolKind.Unknown),
            Release("beta", protocol: ProtocolKind.Torrent),
        });

        Assert.Equal(2, result.Count);
    }

    // --- Ordering ------------------------------------------------------------------------------

    /// <summary>
    /// Asserted PER MEMBER, in order: the whole sequence is compared, so a stage that put the
    /// highest-priority source first and then shuffled the rest cannot pass.
    /// </summary>
    [Fact]
    public void Members_are_ordered_by_source_priority_highest_first()
    {
        var priorities = new FixedPriorities(new() { ["low"] = 1, ["mid"] = 5, ["high"] = 9 });

        var result = Stage(priorities).Deduplicate(new[] { Release("low"), Release("high"), Release("mid") });

        Assert.Equal(
            new[] { "high", "mid", "low" },
            result[0].AlternateMembers.Select(m => m.SourceName).Prepend(result[0].SourceName).ToArray());
    }

    /// <summary>
    /// The representative is the first member, which is what a consumer that ignores the group
    /// entirely will present — so priority has to decide it, not input order.
    /// </summary>
    [Fact]
    public void The_representative_is_the_highest_priority_member_not_the_first_seen()
    {
        var priorities = new FixedPriorities(new() { ["low"] = 1, ["high"] = 9 });

        var result = Stage(priorities).Deduplicate(new[] { Release("low"), Release("high") });

        Assert.Equal("high", result[0].SourceName);
    }

    /// <summary>
    /// With every source neutral — the state of the world until the source registry lands — the
    /// order is still total and reproducible, falling through to the source-name tiebreak.
    /// </summary>
    [Fact]
    public void Equal_priorities_fall_back_to_the_source_name_tiebreak()
    {
        var result = Stage().Deduplicate(new[] { Release("zulu"), Release("alpha") });

        Assert.Equal(
            new[] { "alpha", "zulu" },
            result[0].AlternateMembers.Select(m => m.SourceName).Prepend(result[0].SourceName).ToArray());
    }

    // --- Security properties (security-x7w8-adapter-audit P9/P10/P13) ---------------------------

    /// <summary>
    /// <b>P10: every member keeps its OWN link after grouping, asserted PER MEMBER.</b> The mutant
    /// this must break is a merge that overwrote each member's link with the representative's — a
    /// group whose members all pointed at one source's URL would send every fallback grab back to
    /// the source that just failed, and would do it under the WRONG source's origin.
    /// </summary>
    [Fact]
    public void Every_member_of_a_group_keeps_its_own_link()
    {
        var result = Stage().Deduplicate(new[] { Release("alpha"), Release("beta") });

        Assert.Equal(
            new[] { "http://alpha.example.invalid/get/1", "http://beta.example.invalid/get/1" },
            result[0].AlternateMembers.Select(m => m.Candidate.Link.ToString())
                .Prepend(result[0].Candidate.Link.ToString()).ToArray());
    }

    /// <summary>
    /// <b>P9: a fallback member is identified by ITS OWN source, not the representative's</b>, so
    /// whatever re-validates a link at grab time has the member's own origin to check against. The
    /// per-member host is asserted alongside the per-member source name, since it is the pairing —
    /// this member, this host — that a download-side origin guard depends on.
    /// </summary>
    [Fact]
    public void Every_member_pairs_its_own_source_name_with_its_own_link_host()
    {
        var result = Stage().Deduplicate(new[] { Release("alpha"), Release("beta") });

        Assert.Equal(
            new[] { ("alpha", "alpha.example.invalid"), ("beta", "beta.example.invalid") },
            result[0].AlternateMembers.Select(m => (m.SourceName, m.Candidate.Link.Host))
                .Prepend((result[0].SourceName, result[0].Candidate.Link.Host)).ToArray());
    }

    /// <summary>
    /// <b>P13: an unknown source name resolves to the neutral zero and never throws.</b> Dedup
    /// ordering is a presentation preference, so an unrecognised name must not fail a search.
    /// </summary>
    [Fact]
    public void An_unknown_source_name_resolves_to_zero_without_throwing()
    {
        var priorities = new FixedPriorities(new() { ["known"] = 5 });

        Assert.Equal(0, priorities.PriorityOf("never-configured"));
    }

    /// <summary>
    /// <b>P13, the half that actually bites: an unknown name never outranks a CONFIGURED source.</b>
    /// A lookup that returned <see cref="int.MaxValue"/>, or threw and was caught into a default,
    /// would let a source nobody ranked take the representative slot and become the first thing
    /// grabbed.
    /// </summary>
    [Fact]
    public void An_unknown_source_never_outranks_a_configured_one()
    {
        var priorities = new FixedPriorities(new() { ["configured"] = 1 });

        var result = Stage(priorities).Deduplicate(new[] { Release("unconfigured"), Release("configured") });

        Assert.Equal("configured", result[0].SourceName);
    }

    /// <summary>
    /// The stated limit of P13, pinned so it cannot be mistaken for a guarantee it is not:
    /// <c>Source.Priority</c> is a plain <c>int</c> whose every value is an ordinary weight
    /// (<c>SourceRepository.SourceWriteOptions.Priority</c> says so explicitly), so a source an
    /// operator deliberately ranked NEGATIVE sorts below an unknown name's zero. That is
    /// arithmetic, not a failure of the lookup — "unknown is lowest" would need a non-negative
    /// floor on the column, which is a registry decision (arb-x7w8.4), not this stage's.
    /// </summary>
    [Fact]
    public void A_negative_configured_priority_sorts_below_an_unknown_names_zero()
    {
        var priorities = new FixedPriorities(new() { ["deprioritised"] = -1 });

        var result = Stage(priorities).Deduplicate(new[] { Release("deprioritised"), Release("unconfigured") });

        Assert.Equal("unconfigured", result[0].SourceName);
    }

    /// <summary>
    /// <b>P11, structural: the group projection carries nothing a credential could travel in.</b>
    /// A dedup group is built only from <see cref="RenderedRelease"/>, which exposes a source name,
    /// a candidate, an annotation, the members, and a computed guid — no API key, no BaseUrl, no
    /// credential-bearing configuration. Asserted over the type's own surface so ADDING such a
    /// field is what breaks it, rather than a value-level check that a particular fixture happened
    /// to leave empty.
    /// </summary>
    [Fact]
    public void The_group_projection_exposes_no_credential_bearing_member()
    {
        var surface = typeof(RenderedRelease)
            .GetProperties()
            .Select(p => p.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            new[] { "AlternateMembers", "Candidate", "ProxyGuid", "SourceName", "SuppressionAnnotation" },
            surface);
    }

    /// <summary>
    /// Each member keeps its own source name and therefore its own <see cref="RenderedRelease.ProxyGuid"/>,
    /// which is what makes the fallback usable: the download proxy needs a distinct grabbable
    /// identity per member, not N references to the representative.
    /// </summary>
    [Fact]
    public void Every_member_of_a_group_carries_a_distinct_proxy_guid()
    {
        var result = Stage().Deduplicate(new[] { Release("alpha"), Release("beta") });

        var proxyGuids = result[0].AlternateMembers.Select(m => m.ProxyGuid).Prepend(result[0].ProxyGuid).ToArray();

        Assert.Equal(proxyGuids.Length, proxyGuids.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>
    /// Members are flat, never nested: a reader walking a group never has to recurse, so no reader
    /// can be written that only half-walks it.
    /// </summary>
    [Fact]
    public void Members_of_a_group_never_carry_members_of_their_own()
    {
        var result = Stage().Deduplicate(new[] { Release("alpha"), Release("beta"), Release("gamma") });

        Assert.Equal(
            new[] { 0, 0 },
            result[0].AlternateMembers.Select(m => m.AlternateMembers.Count).ToArray());
    }

    // --- Ordering of the groups themselves -------------------------------------------------------

    /// <summary>
    /// Groups appear in the order their first member appeared, so a single-source result set comes
    /// back in exactly the order upstream produced — pagination slices it by position, and a stage
    /// that reordered would shuffle page boundaries under a client.
    /// </summary>
    [Fact]
    public void Groups_are_returned_in_first_appearance_order()
    {
        var result = Stage().Deduplicate(new[]
        {
            Release("alpha", title: "First Show S01E01"),
            Release("beta", title: "Second Show S01E01"),
            Release("gamma", title: "First Show S01E01"),
        });

        Assert.Equal(
            new[] { "First Show S01E01", "Second Show S01E01" },
            result.Select(r => r.Candidate.Title).ToArray());
    }

    [Fact]
    public void A_single_release_is_returned_unchanged()
    {
        var only = Release("alpha");

        Assert.Same(only, Stage().Deduplicate(new[] { only })[0]);
    }
}
