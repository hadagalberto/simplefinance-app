using SimpleFinance.Domain.Enums;

namespace SimpleFinance.Infrastructure.Notifications;

/// <summary>
/// NOTIF-01 abstraction every notification channel (Telegram, Email) implements. Dispatched by
/// NotificationDispatcher, which selects the channel(s) matching an alert rule's configured
/// NotificationChannelType and gracefully degrades to a no-op for any unconfigured channel.
/// </summary>
public interface INotificationChannel
{
    NotificationChannelType Type { get; }

    /// <summary>True when this channel's required NotificationOptions fields are all present —
    /// pure config inspection, no network I/O.</summary>
    bool IsConfigured { get; }

    /// <summary>
    /// Sends title/message through this channel. <paramref name="recipientEmail"/> is ignored by
    /// TelegramChannel and used as the "To" address by EmailChannel — single-user app, the
    /// recipient IS the app's one User.Email, never a separate configured value. Implementations
    /// never throw for an unconfigured channel (silent no-op); network/provider failures DO
    /// propagate to the caller (NotificationDispatcher.DispatchAsync is the layer that catches
    /// and isolates those per-channel).
    /// </summary>
    Task SendAsync(string title, string message, string recipientEmail, CancellationToken ct);
}
