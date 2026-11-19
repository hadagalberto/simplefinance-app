using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SimpleFinance.Domain.Enums;
using Telegram.Bot;

namespace SimpleFinance.Infrastructure.Notifications;

/// <summary>
/// NOTIF-01 primary alert channel. Send-only — Telegram.Bot 22.x's ReceiveAsync/long-polling
/// surface is intentionally never used here (this app only ever sends outbound messages, per
/// research/STACK.md and the threat model's single outbound trust boundary).
/// </summary>
public sealed class TelegramChannel(
    IOptions<NotificationOptions> options,
    ILogger<TelegramChannel> logger) : INotificationChannel
{
    public NotificationChannelType Type => NotificationChannelType.Telegram;

    public bool IsConfigured =>
        !string.IsNullOrEmpty(options.Value.TelegramBotToken)
        && !string.IsNullOrEmpty(options.Value.TelegramChatId);

    public async Task SendAsync(string title, string message, string recipientEmail, CancellationToken ct)
    {
        if (!IsConfigured)
        {
            logger.LogDebug("Telegram channel not configured — skipping dispatch");
            return;
        }

        var client = new TelegramBotClient(options.Value.TelegramBotToken!);

        await client.SendMessage(
            chatId: options.Value.TelegramChatId!,
            text: $"{title}\n\n{message}",
            cancellationToken: ct);
    }
}
