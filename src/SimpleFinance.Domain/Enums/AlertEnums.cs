namespace SimpleFinance.Domain.Enums;

/// <summary>The 7 alert types NOTIF-02 lists, evaluated by AlertEvaluationJob (a later Phase 8 plan),
/// plus <see cref="AiAnomaly"/> (Phase 18, AI-INS-03) for high-severity anomaly notifications and
/// <see cref="AiMonthlySummary"/> (Phase 19, AI-FCT-03/04) for the monthly summary push.
/// Persisted as <c>int</c> — APPEND-ONLY, never reorder.</summary>
public enum AlertRuleType
{
    BudgetThreshold,
    LowBalance,
    InvoiceDue,
    LargeTransaction,
    NewRecurring,
    SyncError,
    GoalReached,
    AiAnomaly,
    AiMonthlySummary
}

/// <summary>Which channel(s) a Notification/AlertRule dispatches through, per NOTIF-01.</summary>
public enum NotificationChannelType
{
    Telegram,
    Email,
    Both
}
