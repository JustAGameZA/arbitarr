using Arbitarr.Core.Identity;

namespace Arbitarr.Media.Numbering;

/// <summary>
/// Raw, scheme-agnostic numbering as decoded from a release's filename/title, prior to arc binding.
/// </summary>
/// <remarks>
/// This is intentionally *not* <see cref="NumberingCandidate"/>: it is the release-parser's output,
/// still ambiguous as to which scheme actually applies, whereas <see cref="NumberingCandidate"/> is
/// one already-committed interpretation under a specific <see cref="NumberingScheme"/>.
/// </remarks>
/// <param name="SceneSeason">Scene/release-group season number, if the release names one (e.g. the
/// "17" in <c>Bleach-17x36(402)</c>).</param>
/// <param name="SceneEpisode">Scene/release-group episode number relative to <see cref="SceneSeason"/>,
/// if present.</param>
/// <param name="Absolute">Absolute episode number, if the release names one (e.g. the "402" in
/// <c>Bleach-17x36(402)</c>).</param>
/// <param name="ArcTitleToken">A title token from the release that may name a story arc (e.g. a
/// release-group rendering of "Thousand-Year Blood War"), used for arc binding. Null if the release
/// carries no such token.</param>
public sealed record RawReleaseNumbering(
    int? SceneSeason,
    int? SceneEpisode,
    int? Absolute,
    string? ArcTitleToken);

/// <summary>
/// Builds a <see cref="CandidateNumberingSet"/> from a release's raw numbering and (optionally) a
/// series' <see cref="ArcSeasonMap"/>.
/// </summary>
/// <remarks>
/// <para>
/// This is the generation-time enforcement point for AC-M1's regression requirement: the bare
/// <c>(season=1, episode=arc_relative_ep)</c> candidate must never be produced when a release's scene
/// season does not correspond to any TVDB-seasonal season 1 the series actually has. Earlier designs
/// treated this as a downstream filter over an already-generated candidate set; that shape allows the
/// bad candidate to exist transiently and risks a caller consuming the raw set before the filter runs.
/// Generating it correctly the first time removes that risk entirely.
/// </para>
/// <para>
/// Concretely: a scene season/episode pair is only ever turned into an <see cref="NumberingScheme.ArcRelative"/>
/// candidate qualified by whichever <see cref="ArcSeasonBinding"/> it actually binds to (via arc-title
/// token or absolute-range membership). It is never additionally emitted as a bare
/// <see cref="NumberingScheme.TvdbSeasonal"/>-shaped season-1 guess just because scene season numbers
/// commonly start at 1 for arc-numbered anime — that guess is exactly the Bleach S01E36 regression.
/// </para>
/// </remarks>
public static class CandidateNumberingSetBuilder
{
    /// <summary>
    /// Builds the candidate set for a release, binding its scene numbering to arcs via
    /// <paramref name="arcMap"/> when one is available.
    /// </summary>
    /// <param name="raw">The release's raw, scheme-agnostic numbering.</param>
    /// <param name="arcMap">
    /// The series' season-keyed arc-title map, or <see langword="null"/> if none is available (e.g.
    /// no-xem-coverage). When null, only an <see cref="NumberingScheme.Absolute"/> candidate is
    /// generated from <see cref="RawReleaseNumbering.Absolute"/>, if present — arc-relative and
    /// TVDB-seasonal candidates require arc/season context this method does not have without a map.
    /// </param>
    /// <returns>The generated candidate set. Never contains a bare <c>(1, arc_relative_ep)</c> guess.</returns>
    public static CandidateNumberingSet Build(RawReleaseNumbering raw, ArcSeasonMap? arcMap)
    {
        ArgumentNullException.ThrowIfNull(raw);

        var candidates = new List<NumberingCandidate>();

        if (arcMap is not null && raw.SceneSeason is { } sceneSeason && raw.SceneEpisode is { } sceneEpisode)
        {
            var binding = ResolveBinding(raw, arcMap);
            if (binding is not null)
            {
                // Arc-relative candidate qualified by the resolved arc's season — never a bare
                // season-1 guess, since the season here is always the arc's own bound season.
                candidates.Add(new NumberingCandidate(
                    NumberingScheme.ArcRelative,
                    binding.Season,
                    sceneEpisode,
                    raw.Absolute ?? (binding.AbsoluteRangeStart + sceneEpisode - 1)));
            }
            else if (sceneSeason != 1)
            {
                // No arc binding resolved, but the scene season itself is meaningful (not the
                // ambiguous "starts counting from 1 for this arc" convention) — safe to carry
                // through as-is without inventing arc context.
                candidates.Add(new NumberingCandidate(
                    NumberingScheme.ArcRelative,
                    sceneSeason,
                    sceneEpisode,
                    raw.Absolute));
            }

            // Deliberately no `else` branch here: sceneSeason == 1 with no resolved arc binding is
            // exactly the excluded bare (1, arc_relative_ep) shape (AC-M1). Nothing is generated for
            // it — it is not filtered out afterward, it is simply never produced.
        }

        if (raw.Absolute is { } absolute)
        {
            candidates.Add(new NumberingCandidate(NumberingScheme.Absolute, Season: null, absolute, absolute));
        }

        return new CandidateNumberingSet(candidates);
    }

    private static ArcSeasonBinding? ResolveBinding(RawReleaseNumbering raw, ArcSeasonMap arcMap)
    {
        if (raw.ArcTitleToken is { } token)
        {
            var byTitle = arcMap.FindByTitleToken(token);
            if (byTitle is not null)
            {
                return byTitle;
            }
        }

        // Step 3 (docs/step3b-observed-failures.md section 5, "Sennen Kessen hen" row): a release may
        // render an arc's own scene season number under a known alternate convention with no
        // corresponding arc-title token in its own title at all (e.g. the Japanese-language "Sennen
        // Kessen hen" release group renders TYBW as scene season 1, not season 17, and its arc-title
        // token doesn't match TybwBinding's English AlternateArcTitles). Binding by scene season here
        // still requires the season to be a *known* alternate for some binding — an unrecognized
        // season 1 with no title-token match still falls through to the season==1 exclusion below.
        if (raw.SceneSeason is { } sceneSeasonForAlias)
        {
            var bySceneSeason = arcMap.FindBySceneSeason(sceneSeasonForAlias);
            if (bySceneSeason is not null)
            {
                return bySceneSeason;
            }
        }

        if (raw.Absolute is { } absolute)
        {
            var byAbsolute = arcMap.FindByAbsolute(absolute);
            // Deliberately not returning a match here when there is more than one: an absolute-range
            // collision (the real XEM abs-36 case in the Bleach flagship example) must surface as
            // ambiguity to the caller (see AmbiguityPolicy), not be silently resolved to the first
            // candidate found.
            if (byAbsolute.Count == 1)
            {
                return byAbsolute[0];
            }
        }

        return null;
    }
}
