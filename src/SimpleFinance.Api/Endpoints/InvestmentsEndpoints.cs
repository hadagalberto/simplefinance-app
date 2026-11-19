using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using SimpleFinance.Infrastructure.Investments;

namespace SimpleFinance.Api.Endpoints;

/// <summary>
/// INV-01/INV-02: GET /api/investments (positions) and GET /api/investments/summary (totals +
/// distribution) -- thin HTTP wrappers over InvestmentsService (06-03), which owns every bit of
/// actual gain/percent computation. Mirrors DashboardEndpoints.cs's exact structure (group +
/// RequireAuthorization + GetUserId helper). No business logic lives in this file -- each handler
/// resolves InvestmentsService from DI, calls the matching method, and Results.Ok(...)s the
/// response.
/// </summary>
public static class InvestmentsEndpoints
{
    public static void MapInvestmentsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/investments").RequireAuthorization();
        group.MapGet("/", GetPositionsAsync);
        group.MapGet("/summary", GetSummaryAsync);
    }

    private static async Task<IResult> GetPositionsAsync(
        InvestmentsService investmentsService,
        ClaimsPrincipal principal,
        CancellationToken ct)
    {
        var userId = GetUserId(principal);
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        var result = await investmentsService.GetPositionsAsync(userId.Value, ct);
        return Results.Ok(result);
    }

    private static async Task<IResult> GetSummaryAsync(
        InvestmentsService investmentsService,
        ClaimsPrincipal principal,
        CancellationToken ct)
    {
        var userId = GetUserId(principal);
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        var result = await investmentsService.GetSummaryAsync(userId.Value, ct);
        return Results.Ok(result);
    }

    private static Guid? GetUserId(ClaimsPrincipal principal)
    {
        var subClaim = principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
        return subClaim is not null && Guid.TryParse(subClaim, out var userId) ? userId : null;
    }
}
