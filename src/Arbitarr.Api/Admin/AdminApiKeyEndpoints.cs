using Arbitarr.Core.Security;
using Arbitarr.Data.Entities;
using Arbitarr.Data.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing;

namespace Arbitarr.Api.Admin;

/// <summary>
/// One API key as served over the wire. Note what is absent: there is no field for the key value,
/// and deliberately no nullable one a future edit could start populating — the same structural
/// posture <see cref="SourceResponse"/> takes with source keys. The value is not withheld here by
/// care; it is not stored anywhere to withhold (see <see cref="ApiKeyEntry"/>).
/// </summary>
/// <param name="IsLegacy">
/// True for the synthetic row representing the pre-#58 shared key. The issue asks for that key to
/// appear in the list so it is not invisible authority; this flag is how the UI explains why it has
/// no revoke button and no creation date. Since #82 it does: the legacy row renders in place of the
/// revoke button an explanation that the key comes from the server configuration rather than this
/// list, so the missing control reads as deliberate rather than as a bug.
/// </param>
public sealed record ApiKeyResponse(
    long? Id,
    string Label,
    string Scope,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? LastUsedAt,
    DateTimeOffset? RevokedAt,
    bool IsLegacy);

/// <summary>Request body for <c>POST /api/admin/keys</c>.</summary>
public sealed record CreateApiKeyRequest(string? Label, string? Scope);

/// <summary>
/// The response to <c>POST /api/admin/keys</c>, and THE ONLY RESPONSE IN THIS CODEBASE THAT EVER
/// CARRIES A LIVE ADMIN CREDENTIAL.
///
/// <para>It exists because a key the operator cannot read once is a key they cannot use. Everything
/// around it is arranged so this is the only leak point and it is a deliberate, one-shot one: the
/// value is generated in <see cref="ApiKeyRepository.CreateAsync"/>, hashed, and returned here
/// without being persisted, logged, or cached. There is no route that can produce it again — not
/// because one was omitted, but because after this response no copy exists anywhere in the system.
/// The UI says so at the point of creation, since no later screen could — since #82, in the reveal
/// panel in <c>src/Arbitarr.Web/src/surfaces/Settings/ApiKeys/ApiKeys.tsx</c>, which holds the value
/// in component state only and requires an explicit acknowledgement before dismissing it.</para>
/// </summary>
public sealed record CreatedApiKeyResponse(ApiKeyResponse Key, string PlaintextKey);

/// <summary>
/// #58: the admin-gated CRUD surface for named, scoped API keys — create, list, revoke.
///
/// <para><b>EVERY ROUTE IS ADMIN-SCOPED, INCLUDING THE GET.</b> The gate is by path prefix, never by
/// verb, and none of these routes takes <see cref="ApiKeyScope.ReadOnly"/> the way the search and
/// observability reads do. Listing keys tells a caller which labels exist, what authority each
/// holds, and which are dormant — a target list for anyone holding a narrow key, and the wrong
/// thing to hand a monitoring script. Managing credentials requires credential-level authority.</para>
///
/// <para><b>THE SECRET RULE.</b> <see cref="ApiKeyResponse"/> has no field capable of carrying a key
/// value, <see cref="ToResponse"/> is the single projection every read path goes through, and the
/// only type that can carry one — <see cref="CreatedApiKeyResponse"/> — is returned by exactly one
/// handler, from a value that was never written down. No response path in this file other than that
/// one can emit a credential.</para>
///
/// <para><b>THE REQUIRED-BODY TRAP.</b> The body on <see cref="CreateApiKeyAsync"/> is bound
/// OPTIONALLY (<c>[FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)]</c>) and null-checked
/// inside the handler, exactly as <see cref="AdminSecurityEndpoints"/> and
/// <see cref="AdminSourceEndpoints"/> do and for exactly the same reason: a REQUIRED body is
/// model-bound BEFORE endpoint filters run, so a missing or malformed one short-circuits to 400
/// without <see cref="AdminApiKeyFilter"/> ever executing. An unauthenticated remote caller could
/// then tell a malformed body (400) from a well-formed one (503) and enumerate which admin routes
/// exist from outside the gate. Binding optionally keeps the gate strictly first. This is asserted
/// by <c>AdminApiKeyRouteEnumerationTests</c>, which sweeps every concrete admin-mutating route
/// sending NO body at all — that bodilessness is load-bearing and must not be "fixed".</para>
///
/// <para><b>Validation is not duplicated here.</b> <see cref="ApiKeyRepository"/> already rejects
/// empty, over-long and colliding labels, and refuses the last-admin-key revocation (AC5), with the
/// AC24 reject-never-clamp posture. This layer only translates
/// <see cref="ApiKeyValidationException"/> into a 400 — one validation floor, in one place, already
/// tested. The one thing parsed here is the scope string, because an unparseable scope is a
/// wire-format problem that never reaches the repository's typed parameter. That parse matches the
/// scope NAMES explicitly rather than using <c>Enum.TryParse</c>, which would also accept the
/// numeric form and let <c>{"scope":"1"}</c> mint a full-authority key — see the comment at the
/// parse site for why the obvious guards do not close it.</para>
/// </summary>
public static class AdminApiKeyEndpoints
{
    public const string KeysRoute = "/api/admin/keys";

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(KeysRoute, GetKeysAsync)
            .RequireAdminApiKey();

        endpoints.MapPost(KeysRoute, CreateApiKeyAsync)
            .RequireAdminApiKey();

        endpoints.MapDelete($"{KeysRoute}/{{id:long}}", RevokeApiKeyAsync)
            .RequireAdminApiKey();
    }

    /// <summary>
    /// Lists every key, revoked ones included, plus the legacy shared key when one is configured.
    ///
    /// The legacy entry is SYNTHESISED at read time from <see cref="IAdminApiKeyReader"/> reporting
    /// that a value exists — never from a row, because there is none, and never from the value
    /// itself, which is not read here at all. That satisfies the issue's "visible in the list so it
    /// is not invisible authority" without inventing a database row that would then offer a revoke
    /// button that cannot work and a created-date nobody recorded.
    /// </summary>
    private static async Task<IResult> GetKeysAsync(
        ApiKeyRepository repository,
        IAdminApiKeyReader legacyKeyReader,
        CancellationToken cancellationToken)
    {
        var entries = await repository.GetAllAsync(cancellationToken);
        var responses = entries.Select(ToResponse).ToList();

        var legacyKey = await legacyKeyReader.GetCurrentKeyAsync(cancellationToken);
        if (!string.IsNullOrEmpty(legacyKey))
        {
            // Id null: nothing to address. Its dates are null because none was ever recorded — the
            // honest answer, rather than a fabricated CreatedAt that would read as a real one.
            responses.Add(new ApiKeyResponse(
                Id: null,
                Label: DbAdminKeyResolver.LegacyKeyLabel,
                Scope: ApiKeyScope.Admin.ToString(),
                CreatedAt: null,
                LastUsedAt: null,
                RevokedAt: null,
                IsLegacy: true));
        }

        return Results.Ok(responses);
    }

    // Body bound optionally — see the type doc's REQUIRED-BODY TRAP note. Do not make it required.
    private static async Task<IResult> CreateApiKeyAsync(
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] CreateApiKeyRequest? request,
        ApiKeyRepository repository,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return Results.BadRequest(new { error = "A request body with a 'label' property is required." });
        }

        // Parsed here rather than in the repository because an unrecognised string never reaches
        // the repository's typed ApiKeyScope parameter — this is wire-format validation, not domain
        // validation, and the two floors are genuinely different. Case-insensitive, so "readonly"
        // and "ReadOnly" both work; absent means the NARROWER scope, so a caller who forgets the
        // field gets the least authority rather than the most.
        //
        // MATCHED BY NAME, DELIBERATELY — DO NOT "SIMPLIFY" THIS BACK TO Enum.TryParse.
        // TryParse also accepts the enum's NUMERIC form, so {"scope":"1"} would mint an Admin key
        // through an input shape no caller is documented to have and this very error message does
        // not advertise. Adding an Enum.IsDefined check does NOT fix it: 1 IS a defined value, and
        // neither does trimming, since " 1 " and "+1" parse too. The only closed form is matching
        // the two names, so the wire format is closed by construction rather than by a second check
        // that has to anticipate every numeric spelling.
        var scope = ApiKeyScope.ReadOnly;
        if (!string.IsNullOrWhiteSpace(request.Scope))
        {
            if (string.Equals(request.Scope, nameof(ApiKeyScope.ReadOnly), StringComparison.OrdinalIgnoreCase))
            {
                scope = ApiKeyScope.ReadOnly;
            }
            else if (string.Equals(request.Scope, nameof(ApiKeyScope.Admin), StringComparison.OrdinalIgnoreCase))
            {
                scope = ApiKeyScope.Admin;
            }
            else
            {
                return Results.BadRequest(new
                {
                    error = $"'{request.Scope}' is not a valid scope. Use '{nameof(ApiKeyScope.ReadOnly)}' or '{nameof(ApiKeyScope.Admin)}'.",
                });
            }
        }

        try
        {
            var created = await repository.CreateAsync(request.Label ?? string.Empty, scope, cancellationToken);

            // The one and only time the plaintext travels. See CreatedApiKeyResponse.
            return Results.Created(
                $"{KeysRoute}/{created.Entry.Id}",
                new CreatedApiKeyResponse(ToResponse(created.Entry), created.PlaintextKey));
        }
        catch (ApiKeyValidationException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Revokes one key. DELETE rather than a PUT of a revoked flag, because revocation is the only
    /// state change a key supports and there is nothing else on it to edit — but it is a tombstone,
    /// not a row deletion (see <see cref="ApiKeyEntry.RevokedAt"/>).
    ///
    /// Takes no body at all, so there is no model binding to run ahead of the admin filter and no
    /// optional-body treatment is needed — the same reason
    /// <c>AdminSourceEndpoints.TestSourceAsync</c> needs none.
    /// </summary>
    private static async Task<IResult> RevokeApiKeyAsync(
        long id,
        ApiKeyRepository repository,
        CancellationToken cancellationToken)
    {
        try
        {
            var revoked = await repository.RevokeAsync(id, cancellationToken);

            return revoked
                ? Results.NoContent()
                : Results.NotFound(new { error = $"API key {id} does not exist." });
        }
        catch (ApiKeyValidationException ex)
        {
            // AC5: refusing to revoke the last admin-scope key. 400 rather than 409 to match the
            // one translation this codebase already makes for a repository refusal.
            return Results.BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// The SINGLE projection from entity to wire. Every read path goes through here, so the "no key
    /// value ever leaves" property has one home — and <see cref="ApiKeyResponse"/> has no field that
    /// could hold one even if a future caller tried.
    /// </summary>
    private static ApiKeyResponse ToResponse(ApiKeyEntry entry) =>
        new(
            Id: entry.Id,
            Label: entry.Label,
            Scope: entry.Scope.ToString(),
            CreatedAt: entry.CreatedAt,
            LastUsedAt: entry.LastUsedAt,
            RevokedAt: entry.RevokedAt,
            IsLegacy: false);
}
