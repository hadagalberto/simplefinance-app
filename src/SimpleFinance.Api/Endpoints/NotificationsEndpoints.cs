using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using SimpleFinance.Infrastructure.Alerts;

namespace SimpleFinance.Api.Endpoints;

/// <summary>
/// NOTIF-04: GET /api/notifications (list + unread count) and POST /api/notifications/{id}/read
/// (mark-read) -- thin HTTP wrapper over NotificationService, zero business logic here. Mirrors
/// GoalsEndpoints.cs's exact group/RequireAuthorization/GetUserId structure.
///
/// T-08-10-style scoping: every read/write is scoped by the caller's own UserId (via
/// NotificationService's not-found contract) -- an id belonging to another user 404s rather than
/// leaking existence or being mutable (IDOR).
/// </summary>
public static class NotificationsEndpoints
{
    private const int DefaultTake = 50;
    private const int MaxTake = 200;

    public static void MapNotificationsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/notifications").RequireAuthorization();
        group.MapGet("", GetNotificationsAsync);
        group.MapPost("/{id:guid}/read", MarkNotificationReadAsync);
    }

    private static async Task<IResult> GetNotificationsAsync(
        int? take,
        NotificationService notificationService,
        ClaimsPrincipal principal,
        CancellationToken ct)
    {
        var userId = GetUserId(principal);
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        // Capped at MaxTake server-side (200) to prevent an unbounded response regardless of what
        // the client requests; falls back to DefaultTake (50) when the query param is absent or
        // non-positive.
        var effectiveTake = take is > 0 ? Math.Min(take.Value, MaxTake) : DefaultTake;

        var result = await notificationService.GetNotificationsAsync(userId.Value, effectiveTake, ct);
        return Results.Ok(result);
    }

    private static async Task<IResult> MarkNotificationReadAsync(
        Guid id,
        NotificationService notificationService,
        ClaimsPrincipal principal,
        CancellationToken ct)
    {
        var userId = GetUserId(principal);
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        var marked = await notificationService.MarkReadAsync(userId.Value, id, ct);
        return marked ? Results.NoContent() : Results.NotFound();
    }

    private static Guid? GetUserId(ClaimsPrincipal principal)
    {
        var subClaim = principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
        return subClaim is not null && Guid.TryParse(subClaim, out var userId) ? userId : null;
    }
}
