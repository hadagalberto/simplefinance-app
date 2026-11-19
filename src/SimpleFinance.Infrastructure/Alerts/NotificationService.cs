using Microsoft.EntityFrameworkCore;
using SimpleFinance.Infrastructure.Persistence;

namespace SimpleFinance.Infrastructure.Alerts;

/// <summary>A single Notification row for the notification center's list view.</summary>
public sealed record NotificationResult(
    Guid Id,
    string Type,
    string Title,
    string Message,
    string Channel,
    bool IsRead,
    DateTime? SentAt,
    DateTime CreatedAt);

/// <summary>NOTIF-04's GET /api/notifications payload shape -- UnreadCount is computed via a
/// separate CountAsync (not Items.Count(...)) so it is never undercounted once unread
/// notifications exceed the page size Items is capped to.</summary>
public sealed record NotificationsResult(int UnreadCount, IReadOnlyList<NotificationResult> Items);

/// <summary>
/// The real, directly-testable Notification business logic (08-05 Task 2) behind GET
/// /api/notifications and POST /api/notifications/{id}/read -- NotificationsEndpoints.cs is a
/// thin HTTP wrapper over this class only. Deliberately placed in Infrastructure (not
/// Application), mirroring AlertRuleService's/GoalService's documented rationale: it depends
/// directly on SimpleFinanceDbContext, and Application cannot reference Infrastructure without a
/// circular project reference.
/// </summary>
public sealed class NotificationService(SimpleFinanceDbContext db)
{
    public async Task<NotificationsResult> GetNotificationsAsync(Guid userId, int take, CancellationToken ct)
    {
        var unreadCount = await db.Notifications.CountAsync(n => n.UserId == userId && !n.IsRead, ct);

        var items = await db.Notifications
            .Where(n => n.UserId == userId)
            .OrderByDescending(n => n.CreatedAt)
            .Take(take)
            .Select(n => new NotificationResult(
                n.Id,
                n.Type.ToString(),
                n.Title,
                n.Message,
                n.Channel.ToString(),
                n.IsRead,
                n.SentAt,
                n.CreatedAt))
            .ToListAsync(ct);

        return new NotificationsResult(unreadCount, items);
    }

    /// <summary>Mirrors BudgetService.DeleteBudgetAsync's false-not-throw contract (missing/
    /// foreign id). Idempotent no-op write avoidance: only sets IsRead/UpdatedAt and calls
    /// SaveChangesAsync when the row's current IsRead is false, so a repeat click on an
    /// already-read notification never issues an unnecessary UPDATE.</summary>
    public async Task<bool> MarkReadAsync(Guid userId, Guid id, CancellationToken ct)
    {
        var notification = await db.Notifications.FirstOrDefaultAsync(n => n.Id == id && n.UserId == userId, ct);
        if (notification is null)
        {
            return false;
        }

        if (!notification.IsRead)
        {
            notification.IsRead = true;
            notification.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }

        return true;
    }
}
