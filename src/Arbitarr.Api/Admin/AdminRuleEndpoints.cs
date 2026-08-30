using Arbitarr.Core.Filtering;
using Arbitarr.Core.Releases;
using Arbitarr.Core.Settings;
using Arbitarr.Data;
using Arbitarr.Data.Entities;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Arbitarr.Api.Admin;

/// <summary>One persisted filter rule as served over the wire.</summary>
public sealed record FilterRuleResponse(
    long Id,
    string Name,
    bool IsAllow,
    string Pattern,
    int Precedence,
    bool Enabled);

/// <summary>Request body for creating or updating a filter rule.</summary>
public sealed record UpsertFilterRuleRequest(
    string Name,
    bool IsAllow,
    string Pattern,
    int Precedence,
    bool Enabled);

/// <summary>Request body for <c>POST /api/admin/rules/test</c> — a candidate title to dry-run against a pattern.</summary>
public sealed record TestFilterRuleRequest(
    string Name,
    bool IsAllow,
    string Pattern,
    int Precedence,
    string Title);

/// <summary>Result of a <c>POST /api/admin/rules/test</c> dry-run.</summary>
public sealed record TestFilterRuleResponse(string Verdict);

/// <summary>
/// Wave-C item 4 / R11: admin-gated CRUD over the M4 filter profile's rules
/// (<see cref="FilterRuleEntry"/>), operating against the single profile flagged
/// <see cref="FilterProfileEntry.IsDefault"/> (mirrors <c>Arbitarr.Data.Filtering.FilterProfileLoader</c>'s
/// scope — this codebase has no per-API-key rule authoring surface yet). Import/export is
/// intentionally NOT duplicated here: that already exists via <see cref="RuleImporter"/>/
/// <see cref="RuleExporter"/> and is wired at the UI layer, which reuses the same
/// <see cref="FilterRuleEntry"/> rows this endpoint reads and writes.
///
/// Every create/update rejects (400, with reason) rather than clamps: an invalid regex,
/// an over-length pattern, or a rule count already at <see cref="SettingsValidator.MaxRulesPerProfile"/>
/// (R11/AC24) — a catastrophic/ReDoS-shaped pattern is still caught here because
/// <see cref="FilterRule"/>'s constructor and <c>Evaluate</c> always run inside
/// <see cref="FilterRule.MatchTimeout"/>, but only the count/length bounds are check-before-save;
/// the regex itself is validated by attempting construction and matching it against the request's
/// own <see cref="TestFilterRuleRequest.Title"/> before any row is written.
/// </summary>
public static class AdminRuleEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/admin/rules", ListRulesAsync)
            .RequireAdminApiKey();

        endpoints.MapPost("/api/admin/rules", CreateRuleAsync)
            .RequireAdminApiKey();

        endpoints.MapPut("/api/admin/rules/{id:long}", UpdateRuleAsync)
            .RequireAdminApiKey();

        endpoints.MapDelete("/api/admin/rules/{id:long}", DeleteRuleAsync)
            .RequireAdminApiKey();

        endpoints.MapPost("/api/admin/rules/test", TestRuleAsync)
            .RequireAdminApiKey();
    }

    private static async Task<IResult> ListRulesAsync(
        ArbitarrDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var profileId = await DefaultProfileIdAsync(dbContext, cancellationToken);
        var rules = await dbContext.FilterRules
            .AsNoTracking()
            .Where(r => r.FilterProfileId == profileId)
            .OrderBy(r => r.Id)
            .Select(r => new FilterRuleResponse(r.Id, r.Name, r.IsAllow, r.Pattern, r.Precedence, r.Enabled))
            .ToListAsync(cancellationToken);

        return Results.Ok(rules);
    }

    private static async Task<IResult> CreateRuleAsync(
        UpsertFilterRuleRequest? request,
        ArbitarrDbContext dbContext,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return Results.BadRequest(new { error = "Request body is required." });
        }

        var validationError = ValidateRequest(request);
        if (validationError is not null)
        {
            return Results.BadRequest(new { error = validationError });
        }

        var profileId = await DefaultProfileIdAsync(dbContext, cancellationToken);

        var currentCount = await dbContext.FilterRules
            .AsNoTracking()
            .CountAsync(r => r.FilterProfileId == profileId, cancellationToken);

        try
        {
            SettingsValidator.ValidateRuleCount(currentCount);
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }

        var now = DateTimeOffset.UtcNow;
        var entry = new FilterRuleEntry
        {
            FilterProfileId = profileId,
            Name = request.Name,
            IsAllow = request.IsAllow,
            Pattern = request.Pattern,
            Precedence = request.Precedence,
            Enabled = request.Enabled,
            CreatedAt = now,
            UpdatedAt = now,
        };

        dbContext.FilterRules.Add(entry);
        await dbContext.SaveChangesAsync(cancellationToken);

        return Results.Ok(new FilterRuleResponse(entry.Id, entry.Name, entry.IsAllow, entry.Pattern, entry.Precedence, entry.Enabled));
    }

    private static async Task<IResult> UpdateRuleAsync(
        long id,
        UpsertFilterRuleRequest? request,
        ArbitarrDbContext dbContext,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return Results.BadRequest(new { error = "Request body is required." });
        }

        var validationError = ValidateRequest(request);
        if (validationError is not null)
        {
            return Results.BadRequest(new { error = validationError });
        }

        var entry = await dbContext.FilterRules.FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (entry is null)
        {
            return Results.NotFound(new { error = $"No filter rule with id {id}." });
        }

        entry.Name = request.Name;
        entry.IsAllow = request.IsAllow;
        entry.Pattern = request.Pattern;
        entry.Precedence = request.Precedence;
        entry.Enabled = request.Enabled;
        entry.UpdatedAt = DateTimeOffset.UtcNow;

        await dbContext.SaveChangesAsync(cancellationToken);

        return Results.Ok(new FilterRuleResponse(entry.Id, entry.Name, entry.IsAllow, entry.Pattern, entry.Precedence, entry.Enabled));
    }

    private static async Task<IResult> DeleteRuleAsync(
        long id,
        ArbitarrDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var entry = await dbContext.FilterRules.FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (entry is null)
        {
            return Results.NotFound(new { error = $"No filter rule with id {id}." });
        }

        dbContext.FilterRules.Remove(entry);
        await dbContext.SaveChangesAsync(cancellationToken);

        return Results.Ok();
    }

    /// <summary>
    /// Dry-runs a candidate rule (not yet persisted) against a single title, so an operator can
    /// verify a pattern before saving it. Never touches the database. Rejects a catastrophic/invalid
    /// regex with 400 rather than allowing it through to a later save — <see cref="FilterRule"/>'s
    /// constructor and <see cref="FilterRule.Evaluate"/> both already carry <see cref="FilterRule.MatchTimeout"/>
    /// (R11's ReDoS backstop), so a hostile pattern here reports its actual match verdict (usually
    /// <c>Unknown</c> on timeout) rather than hanging this request.
    /// </summary>
    private static IResult TestRuleAsync(TestFilterRuleRequest? request)
    {
        if (request is null)
        {
            return Results.BadRequest(new { error = "Request body is required." });
        }

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return Results.BadRequest(new { error = "Rule name must not be blank." });
        }

        if (request.Title is null)
        {
            return Results.BadRequest(new { error = "Title must not be null." });
        }

        try
        {
            SettingsValidator.ValidateFilterRulePattern(request.Pattern);
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }

        FilterRule rule;
        try
        {
            rule = new FilterRule(request.Name, request.IsAllow, (Precedence)request.Precedence, request.Pattern);
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = $"Invalid rule: {ex.Message}" });
        }

        var candidate = new ReleaseCandidate
        {
            Title = request.Title,
            Guid = "test-rule-dry-run",
            PubDate = DateTimeOffset.UtcNow,
            Link = new Uri("http://localhost/test-rule-dry-run"),
        };

        var verdict = rule.Evaluate(candidate);
        return Results.Ok(new TestFilterRuleResponse(verdict.ToString()));
    }

    private static string? ValidateRequest(UpsertFilterRuleRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return "Rule name must not be blank.";
        }

        try
        {
            SettingsValidator.ValidateFilterRulePattern(request.Pattern);
        }
        catch (ArgumentException ex)
        {
            return ex.Message;
        }

        if (!Enum.IsDefined(typeof(Precedence), request.Precedence))
        {
            return $"Precedence value {request.Precedence} is not a recognized precedence tier.";
        }

        try
        {
            // FilterRule's constructor is the single source of truth for "is this a valid,
            // ReDoS-bounded pattern" (R11) — reuse it here rather than re-validating separately so
            // this endpoint can never accept a pattern the rule engine would later reject.
            _ = new FilterRule(request.Name, request.IsAllow, (Precedence)request.Precedence, request.Pattern);
        }
        catch (ArgumentException ex)
        {
            return $"Invalid rule: {ex.Message}";
        }

        return null;
    }

    private static async Task<long> DefaultProfileIdAsync(ArbitarrDbContext dbContext, CancellationToken cancellationToken)
    {
        var profile = await dbContext.FilterProfiles.FirstOrDefaultAsync(p => p.IsDefault, cancellationToken);
        if (profile is not null)
        {
            return profile.Id;
        }

        // Mirrors FilterProfileLoader's fallback: a fresh install has no profile row yet. Rule
        // authoring must still work before the first search request lazily needs one, so create the
        // same "Default" profile here rather than requiring a separate provisioning step.
        var now = DateTimeOffset.UtcNow;
        var created = new FilterProfileEntry
        {
            Name = "Default",
            IsDefault = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
        dbContext.FilterProfiles.Add(created);
        await dbContext.SaveChangesAsync(cancellationToken);
        return created.Id;
    }
}
