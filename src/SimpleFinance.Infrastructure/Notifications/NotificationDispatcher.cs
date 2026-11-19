using Microsoft.Extensions.Logging;
using SimpleFinance.Domain.Enums;

namespace SimpleFinance.Infrastructure.Notifications;

/// <summary>
/// NOTIF-01 dispatch orchestration: resolves the channel set from a NotificationChannelType
/// selection (Telegram/Email -> the single matching channel; Both -> every registered channel)
/// and dispatches to each. T-08-03 (mitigate): a per-channel try/catch means one channel's
/// exception (network failure, invalid token) never propagates and never blocks the sibling
/// channel or the caller (AlertEvaluationJob, a later Phase 8 plan) -- DispatchAsync itself
/// never throws.
/// </summary>
public sealed class NotificationDispatcher(
    IEnumerable<INotificationChannel> channels,
    ILogger<NotificationDispatcher> logger)
{
    public async Task DispatchAsync(
        NotificationChannelType selection,
        string title,
        string message,
        string recipientEmail,
        CancellationToken ct)
    {
        var targets = selection == NotificationChannelType.Both
            ? channels
            : channels.Where(c => c.Type == selection);

        foreach (var channel in targets)
        {
            if (!channel.IsConfigured)
            {
                logger.LogDebug(
                    "Notification channel {ChannelType} not configured — skipping dispatch",
                    channel.Type);
                continue;
            }

            try
            {
                await channel.SendAsync(title, message, recipientEmail, ct);
            }
            catch (Exception ex)
            {
                logger.LogError(
                    ex,
                    "Notification channel {ChannelType} failed to dispatch — continuing to next channel",
                    channel.Type);
            }
        }
    }

    /// <summary>First channel matching <paramref name="type"/>, or null when Both is passed (no
    /// single matching channel) or no channel of that type is registered. Used by a later Phase 8
    /// plan's per-channel "test notification" endpoint.</summary>
    public INotificationChannel? GetChannel(NotificationChannelType type)
    {
        if (type == NotificationChannelType.Both)
        {
            return null;
        }

        return channels.FirstOrDefault(c => c.Type == type);
    }
}
