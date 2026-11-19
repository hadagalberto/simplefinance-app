namespace SimpleFinance.Domain.Enums;

/// <summary>Processing status of a persisted WebhookEvent row.</summary>
public enum WebhookEventStatus
{
    Pending,
    Processed,
    Failed
}

/// <summary>
/// What initiated a sync run — exactly PRD §11.2's 3-value <c>TriggeredBy</c> schema.
/// Note: the "first sync on connect" triggered when a Connection is created (02-07) uses
/// <see cref="Manual"/>, since PRD's schema has no dedicated 4th "OnConnect" value.
/// </summary>
public enum SyncTrigger
{
    Webhook,
    Cron,
    Manual
}

/// <summary>Outcome status of a single SyncLog run.</summary>
public enum SyncStatus
{
    Running,
    Success,
    PartialSuccess,
    Failed
}
