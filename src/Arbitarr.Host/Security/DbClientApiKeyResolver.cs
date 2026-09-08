using Arbitarr.Core.Security;
using Arbitarr.Data.Security;
using Microsoft.Extensions.DependencyInjection;

namespace Arbitarr.Host.Security;

/// <summary>
/// #97: the <see cref="IClientApiKeyResolver"/> the search routes actually see. It admits a key
/// minted in Settings > API keys (#58) as well as the environment keys, closing the gap the issue
/// reports: before this, a minted key pasted into Sonarr was answered with Newznab error 100 while
/// the compose-file placeholder worked, and the API keys section said the opposite.
///
/// <para><b>ORDER: MINTED KEYS FIRST, ENVIRONMENT SECOND.</b> Same ordering, and the same reason, as
/// <see cref="DbCredentialResolver"/>: the more specific answer wins, so a minted key always resolves
/// to its own label and id rather than being absorbed by a colliding environment value. Values do
/// not collide in practice (<see cref="ApiKeyHasher.Generate"/> mints 256 random bits), but ordering
/// by specificity costs nothing and removes the question. The rejected alternative was environment
/// first — it would make the environment key shadow a minted one on collision, which is the
/// direction that loses attribution rather than the direction that keeps it.</para>
///
/// <para><b>THE ENVIRONMENT KEYS ARE NOT DEPRECATED HERE, AND ARE NOT AUTO-MIGRATED.</b> An upgrade
/// that silently invalidates the credential every *arr instance on a homelab box is currently using,
/// while the operator is not watching, is the worst available failure — it is exactly the reasoning
/// <see cref="DbCredentialResolver"/> records for the legacy admin key, and it applies unchanged.
/// <c>Arbitarr:ApiKey</c> and <c>Arbitarr:ClientApiKeys</c> keep working after this change; minting
/// per-client keys is the recommendation, not a requirement.</para>
///
/// <para><b>ANY SCOPE SATISFIES A SEARCH ROUTE — AN ADMIN KEY IS NOT REQUIRED.</b> There is
/// deliberately no <see cref="ApiKeyScope"/> comparison below. The search and download routes are
/// <c>RouteClassification.PublicRead</c>: they read, they mutate nothing, and requiring
/// <see cref="ApiKeyScope.Admin"/> for them would force an operator to hand Sonarr a credential that
/// also reaches every mutating admin route — the precise outcome #58 exists to end. A
/// <see cref="ApiKeyScope.ReadOnly"/> key is therefore the right thing to hand an *arr client, which
/// is what the API keys section says; an <see cref="ApiKeyScope.Admin"/> key is accepted too,
/// because it reaches everything a read key does by definition.</para>
///
/// <para><b>SCOPE LIFETIME.</b> Registered as a singleton (the endpoints resolve it once per
/// request from the root provider, and <see cref="ConfiguredClientApiKeyResolver"/> is immutable),
/// so it resolves its own scope per call rather than capturing <c>ArbitarrDbContext</c>, which is
/// scoped and not thread-safe. This is the same shape <see cref="ThrottledApiKeyLastUsedRecorder"/>
/// and <c>ScopedEventSink</c> use, and for the same reason.</para>
/// </summary>
public sealed class DbClientApiKeyResolver : IClientApiKeyResolver
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ConfiguredClientApiKeyResolver _environmentKeys;
    private readonly IApiKeyLastUsedRecorder _lastUsedRecorder;

    public DbClientApiKeyResolver(
        IServiceScopeFactory scopeFactory,
        ConfiguredClientApiKeyResolver environmentKeys,
        IApiKeyLastUsedRecorder lastUsedRecorder)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _environmentKeys = environmentKeys ?? throw new ArgumentNullException(nameof(environmentKeys));
        _lastUsedRecorder = lastUsedRecorder ?? throw new ArgumentNullException(nameof(lastUsedRecorder));
    }

    /// <inheritdoc />
    public async Task<ClientKeyContext?> ResolveAsync(string? apikey, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(apikey))
        {
            return null;
        }

        using var scope = _scopeFactory.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<ApiKeyRepository>();

        // FindLiveByPresentedKeyAsync is the ONE verification path for a minted key, shared with
        // DbCredentialResolver. Reusing it unchanged is the point: a second hashing path here would
        // be a second place for the stored shape to drift from, and its own doc comment already
        // records why the comparison is an indexed equality on the SHA-256 hash rather than
        // ApiKeyHasher.HashesMatch — do not "harden" that here without reading it first.
        // "Live" is the whole revocation story — a revoked row is not returned, so a revoked key
        // takes effect on the very next request with no cache to invalidate.
        var minted = await repository.FindLiveByPresentedKeyAsync(apikey, cancellationToken)
            .ConfigureAwait(false);

        if (minted is not null)
        {
            // Attribution parity with the admin gate: the same throttled, off-request-path recorder,
            // so "is anything still using this?" is answerable before revoking a client key exactly
            // as it is for an admin key.
            _lastUsedRecorder.RecordUsed(minted.Id);
            return new ClientKeyContext(minted.Label, minted.Id);
        }

        return _environmentKeys.Resolve(apikey);
    }
}
