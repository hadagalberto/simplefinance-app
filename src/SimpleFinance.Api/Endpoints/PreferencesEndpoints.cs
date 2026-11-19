using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using SimpleFinance.Infrastructure.Preferences;

namespace SimpleFinance.Api.Endpoints;

/// <summary>
/// CFG-02: GET/PUT /api/preferences -- thin HTTP wrappers over PreferencesService, which owns
/// every bit of actual business logic (get-or-create, validation, live Hangfire reschedule).
/// Mirrors BudgetsEndpoints.cs's exact structure (group + RequireAuthorization + GetUserId
/// helper). No business logic lives in this file.
/// </summary>
public static class PreferencesEndpoints
{
    public static void MapPreferencesEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/preferences").RequireAuthorization();
        group.MapGet("", GetPreferencesAsync);
        group.MapPut("", UpdatePreferencesAsync);
    }

    private static async Task<IResult> GetPreferencesAsync(
        PreferencesService preferencesService,
        ClaimsPrincipal principal,
        CancellationToken ct)
    {
        var userId = GetUserId(principal);
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        var result = await preferencesService.GetPreferencesAsync(userId.Value, ct);
        return Results.Ok(result);
    }

    private static async Task<IResult> UpdatePreferencesAsync(
        UpdatePreferencesRequest body,
        PreferencesService preferencesService,
        ClaimsPrincipal principal,
        CancellationToken ct)
    {
        var userId = GetUserId(principal);
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        var result = await preferencesService.UpdatePreferencesAsync(
            userId.Value, body.Timezone, body.BaseCurrency, body.SyncCronExpression, body.DefaultTheme, ct);

        // PreferencesService never throws for expected validation failures -- null means one of
        // timezone/syncCronExpression/defaultTheme was invalid; no row was mutated.
        return result is null
            ? Results.BadRequest(new { error = "timezone, syncCronExpression, or defaultTheme is invalid." })
            : Results.Ok(result);
    }

    private static Guid? GetUserId(ClaimsPrincipal principal)
    {
        var subClaim = principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
        return subClaim is not null && Guid.TryParse(subClaim, out var userId) ? userId : null;
    }
}

/// <summary>JSON body shape for PUT /api/preferences. Public (not nested-private) so minimal
/// API's JSON body binder can deserialize into it without relying on reflection-only access to
/// a private type.</summary>
public sealed record UpdatePreferencesRequest(string Timezone, string BaseCurrency, string SyncCronExpression, string DefaultTheme);
