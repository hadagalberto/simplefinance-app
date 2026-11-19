namespace SimpleFinance.Infrastructure.Notifications;

/// <summary>
/// Binds to the "Notifications" configuration section (Notifications__* — see .env.example).
/// Every field is optional: NOTIF-01/08-CONTEXT.md require the app to run correctly with ZERO
/// channels configured (the common first-run state) — TelegramChannel/EmailChannel each derive
/// IsConfigured purely from these fields, never throwing when unset.
///
/// Property names are chosen so every secret-shaped value naturally matches
/// SecretRedactionDestructuringPolicy's case-insensitive substring list ("token"/"password") —
/// TelegramBotToken and SmtpPassword auto-redact on any accidental structured log call
/// (T-08-04), no changes to the redaction policy itself are needed.
/// </summary>
public class NotificationOptions
{
    public const string SectionName = "Notifications";

    /// <summary>Telegram bot token from @BotFather. Matches "token" — auto-redacted in logs.</summary>
    public string? TelegramBotToken { get; set; }

    /// <summary>Target chat id the bot sends messages to (the app's one User).</summary>
    public string? TelegramChatId { get; set; }

    public string? SmtpHost { get; set; }

    public int SmtpPort { get; set; } = 587;

    public string? SmtpUser { get; set; }

    /// <summary>Matches "password" — auto-redacted in logs.</summary>
    public string? SmtpPassword { get; set; }

    public string? SmtpFromAddress { get; set; }

    public string SmtpFromName { get; set; } = "SimpleFinance";

    public bool SmtpUseSsl { get; set; } = true;
}
