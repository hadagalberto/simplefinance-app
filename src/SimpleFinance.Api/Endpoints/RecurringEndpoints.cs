using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using SimpleFinance.Infrastructure.Recurring;

namespace SimpleFinance.Api.Endpoints;

/// <summary>
/// RECUR-02/03/04: GET /api/recurring (panel data shape) and PATCH /api/recurring/{id}/subscription
/// + PATCH /api/recurring/{id}/active (user toggles) -- thin HTTP wrappers over RecurringService
/// (07-03 Task 2), which owns every bit of actual computation/mutation. Mirrors
/// TransactionsEndpoints.cs's exact group/RequireAuthorization/GetUserId structure. No business
/// logic lives in this file.
///
/// T-07-04: every PATCH route is scoped by both id and the caller's own UserId (via
/// RecurringService's not-found contract) -- an id belonging to another user 404s rather than
/// leaking existence or being mutable (IDOR).
/// </summary>
public static class RecurringEndpoints
{
    public static void MapRecurringEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/recurring").RequireAuthorization();
        group.MapGet("", GetRecurringAsync);
        group.MapPatch("/{id:guid}/subscription", ToggleSubscriptionAsync);
        group.MapPatch("/{id:guid}/active", ToggleActiveAsync);
    }

    private static async Task<IResult> GetRecurringAsync(
        RecurringService recurringService,
        ClaimsPrincipal principal,
        CancellationToken ct)
    {
        var userId = GetUserId(principal);
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        var result = await recurringService.GetRecurringAsync(userId.Value, ct);
        return Results.Ok(result);
    }

    private static async Task<IResult> ToggleSubscriptionAsync(
        Guid id,
        ToggleSubscriptionRequest request,
        RecurringService recurringService,
        ClaimsPrincipal principal,
        CancellationToken ct)
    {
        var userId = GetUserId(principal);
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        var success = await recurringService.ToggleSubscriptionAsync(userId.Value, id, request.IsSubscription, ct);
        return success ? Results.Ok() : Results.NotFound();
    }

    private static async Task<IResult> ToggleActiveAsync(
        Guid id,
        ToggleActiveRequest request,
        RecurringService recurringService,
        ClaimsPrincipal principal,
        CancellationToken ct)
    {
        var userId = GetUserId(principal);
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        var success = await recurringService.ToggleActiveAsync(userId.Value, id, request.IsActive, ct);
        return success ? Results.Ok() : Results.NotFound();
    }

    private static Guid? GetUserId(ClaimsPrincipal principal)
    {
        var subClaim = principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
        return subClaim is not null && Guid.TryParse(subClaim, out var userId) ? userId : null;
    }
}

public sealed record ToggleSubscriptionRequest(bool IsSubscription);

public sealed record ToggleActiveRequest(bool IsActive);
