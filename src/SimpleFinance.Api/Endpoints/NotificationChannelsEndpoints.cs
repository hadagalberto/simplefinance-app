using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using SimpleFinance.Domain.Enums;
using SimpleFinance.Infrastructure.Notifications;
using SimpleFinance.Infrastructure.Persistence;

namespace SimpleFinance.Api.Endpoints;

/// <summary>
/// NOTIF-01: GET /api/notification-channels (real IsConfigured status per channel) and
/// POST /api/notification-channels/{type}/test (a real, user-triggered send -- never a silent
/// "succeeds" for a channel that can't actually send). Mirrors GoalsEndpoints.cs's exact
/// group/RequireAuthorization/GetUserId structure.
///
/// T-08-12 (accept): the test message content is fixed, non-sensitive boilerplate text; the
/// endpoint requires authentication so only the app's own single user can trigger it.
/// </summary>
public static class NotificationChannelsEndpoints
{
    public static void MapNotificationChannelsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/notification-channels").RequireAuthorization();
        group.MapGet("", GetChannelsAsync);
        group.MapPost("/{type}/test", TestChannelAsync);
    }

    private static Task<IResult> GetChannelsAsync(
        IEnumerable<INotificationChannel> channels,
        ClaimsPrincipal principal,
        CancellationToken ct)
    {
        var userId = GetUserId(principal);
        if (userId is null)
        {
            return Task.FromResult(Results.Unauthorized());
        }

        var response = channels
            .Select(c => new { type = c.Type.ToString(), isConfigured = c.IsConfigured })
            .ToList();

        return Task.FromResult(Results.Ok(response));
    }

    private static async Task<IResult> TestChannelAsync(
        string type,
        NotificationDispatcher dispatcher,
        SimpleFinanceDbContext db,
        ClaimsPrincipal principal,
        CancellationToken ct)
    {
        var userId = GetUserId(principal);
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        // Both isn't a real channel to test -- it's a dispatch-time fan-out selector
        // (NotificationDispatcher.GetChannel(Both) returns null by design, 08-02 precedent).
        if (!Enum.TryParse<NotificationChannelType>(type, ignoreCase: true, out var channelType)
            || channelType == NotificationChannelType.Both)
        {
            return Results.BadRequest(new { error = "invalid_channel_type" });
        }

        var channel = dispatcher.GetChannel(channelType);
        if (channel is null || !channel.IsConfigured)
        {
            return Results.BadRequest(new { error = "channel_not_configured" });
        }

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId.Value, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        // Unlike AlertEvaluationJob's fire-and-forget NotificationDispatcher.DispatchAsync call,
        // this is an explicit user-triggered test action -- the caller needs to know if it
        // genuinely failed, so SendAsync is called directly (not through the dispatcher's
        // swallow-and-log try/catch) and a real failure surfaces as a 502.
        try
        {
            await channel.SendAsync(
                "Test notification",
                "This is a test notification from SimpleFinance.",
                user.Email,
                ct);
            return Results.Ok(new { sent = true });
        }
        catch (Exception ex)
        {
            return Results.Json(new { sent = false, error = ex.Message }, statusCode: StatusCodes.Status502BadGateway);
        }
    }

    private static Guid? GetUserId(ClaimsPrincipal principal)
    {
        var subClaim = principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
        return subClaim is not null && Guid.TryParse(subClaim, out var userId) ? userId : null;
    }
}
