using SimpleFinance.Domain.Common;
using SimpleFinance.Domain.Enums;

namespace SimpleFinance.Domain.Entities;

/// <summary>
/// An in-app/dispatched notification for NOTIF-04's notification center. Created by
/// AlertEvaluationJob (a later Phase 8 plan) whenever an AlertRule fires.
/// </summary>
public class Notification : UserOwnedEntity
{
    public AlertRuleType Type { get; set; }

    public string Title { get; set; } = string.Empty;

    public string Message { get; set; } = string.Empty;

    public NotificationChannelType Channel { get; set; }

    public bool IsRead { get; set; }

    public DateTime? SentAt { get; set; }
}
