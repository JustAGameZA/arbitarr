namespace Arbitarr.Core.Identity;

/// <summary>
/// Optional hints that narrow identity resolution when a bare title is ambiguous.
/// </summary>
/// <param name="TvdbId">A known TVDB ID to resolve directly, bypassing title search.</param>
/// <param name="TmdbId">A known TMDB ID to resolve directly, bypassing title search.</param>
/// <param name="Year">Release/premiere year, used to disambiguate same-titled series.</param>
public sealed record IdentityResolutionHints(int? TvdbId, int? TmdbId, int? Year);

/// <summary>
/// Resolves a canonical <see cref="SeriesIdentity"/> from a display title and optional hints.
/// </summary>
/// <remarks>
/// <para>
/// Exists because of the Ghost in the Shell franchise-disambiguation problem: a bare title such as
/// "The Ghost in the Shell" is not sufficient to identify which sibling series a release belongs to.
/// Implementations resolve the canonical identity (TVDB/TMDB ID plus alternate titles) that
/// downstream matching uses instead of the display title. This interface defines the contract only —
/// no resolution logic ships here; see the Step 3a implementations in <c>Arbitarr.Media</c>.
/// </para>
/// </remarks>
public interface IIdentityResolver
{
    /// <summary>
    /// Attempts to resolve a canonical series identity for the given title and hints.
    /// </summary>
    /// <param name="title">The display title to resolve, as it appears in a release or search query.</param>
    /// <param name="hints">Optional hints (known provider IDs, year) that narrow resolution.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>The resolved identity, or <see langword="null"/> if no confident match was found.</returns>
    Task<SeriesIdentity?> ResolveAsync(
        string title,
        IdentityResolutionHints hints,
        CancellationToken cancellationToken = default);
}
