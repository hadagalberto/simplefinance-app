using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using SimpleFinance.Infrastructure.NetWorth;

namespace SimpleFinance.Api.Endpoints;

/// <summary>
/// INV-03/INV-04/INV-05: GET /api/networth/summary (current composition) and GET
/// /api/networth/evolution (30/90/365-day range) -- thin HTTP wrappers over NetWorthService
/// (06-04), which owns every bit of actual computation. Mirrors InvestmentsEndpoints.cs's exact
/// structure (group + RequireAuthorization + GetUserId helper). No business logic lives in this
/// file -- each handler resolves NetWorthService from DI, calls the matching method, and
/// Results.Ok(...)s the response.
///
/// T-06-08: evolution's `days` query param is restricted to exactly {30, 90, 365} (400
/// otherwise) -- mirrors DashboardEndpoints.GetNetWorthEvolutionAsync's `months` validation
/// pattern exactly, applied to this route's 3-way choice, so an arbitrarily large caller-supplied
/// range never reaches a SQL range query.
/// </summary>
public static class NetWorthEndpoints
{
    public static void MapNetWorthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/networth").RequireAuthorization();
        group.MapGet("/summary", GetSummaryAsync);
        group.MapGet("/evolution", GetEvolutionAsync);
    }

    private static async Task<IResult> GetSummaryAsync(
        NetWorthService netWorthService,
        ClaimsPrincipal principal,
        CancellationToken ct)
    {
        var userId = GetUserId(principal);
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        var result = await netWorthService.GetSummaryAsync(userId.Value, ct);
        return Results.Ok(result);
    }

    private static async Task<IResult> GetEvolutionAsync(
        int? days,
        NetWorthService netWorthService,
        ClaimsPrincipal principal,
        CancellationToken ct)
    {
        var userId = GetUserId(principal);
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        var resolvedDays = days ?? 30;
        if (resolvedDays != 30 && resolvedDays != 90 && resolvedDays != 365)
        {
            return Results.BadRequest(new { error = "days must be exactly 30, 90, or 365." });
        }

        var result = await netWorthService.GetEvolutionAsync(userId.Value, resolvedDays, ct);
        return Results.Ok(result);
    }

    private static Guid? GetUserId(ClaimsPrincipal principal)
    {
        var subClaim = principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
        return subClaim is not null && Guid.TryParse(subClaim, out var userId) ? userId : null;
    }
}
