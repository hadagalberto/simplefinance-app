using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;
using SimpleFinance.Domain.Enums;

namespace SimpleFinance.Infrastructure.Notifications;

/// <summary>
/// NOTIF-01 secondary/optional alert channel (SMTP via MailKit, never
/// System.Net.Mail.SmtpClient — see research/STACK.md's "What NOT to Use").
/// </summary>
public sealed class EmailChannel(
    IOptions<NotificationOptions> options,
    ILogger<EmailChannel> logger) : INotificationChannel
{
    public NotificationChannelType Type => NotificationChannelType.Email;

    public bool IsConfigured =>
        !string.IsNullOrEmpty(options.Value.SmtpHost)
        && !string.IsNullOrEmpty(options.Value.SmtpFromAddress);

    public async Task SendAsync(string title, string message, string recipientEmail, CancellationToken ct)
    {
        if (!IsConfigured)
        {
            logger.LogDebug("Email channel not configured — skipping dispatch");
            return;
        }

        var opts = options.Value;

        var mimeMessage = new MimeMessage();
        mimeMessage.From.Add(new MailboxAddress(opts.SmtpFromName, opts.SmtpFromAddress!));
        mimeMessage.To.Add(MailboxAddress.Parse(recipientEmail));
        mimeMessage.Subject = title;
        mimeMessage.Body = new TextPart("plain") { Text = message };

        using var client = new SmtpClient();

        await client.ConnectAsync(
            opts.SmtpHost!,
            opts.SmtpPort,
            opts.SmtpUseSsl ? SecureSocketOptions.StartTls : SecureSocketOptions.None,
            ct);

        if (!string.IsNullOrEmpty(opts.SmtpUser) && !string.IsNullOrEmpty(opts.SmtpPassword))
        {
            await client.AuthenticateAsync(opts.SmtpUser, opts.SmtpPassword, ct);
        }

        await client.SendAsync(mimeMessage, ct);
        await client.DisconnectAsync(true, ct);
    }
}
