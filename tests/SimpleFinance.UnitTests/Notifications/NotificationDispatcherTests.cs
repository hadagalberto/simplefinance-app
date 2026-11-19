using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SimpleFinance.Domain.Enums;
using SimpleFinance.Infrastructure.Notifications;

namespace SimpleFinance.UnitTests.Notifications;

/// <summary>
/// Task 1 (08-02): TelegramChannel/EmailChannel IsConfigured tests -- pure config inspection, no
/// network I/O. Both channels are constructed directly with a hand-built
/// IOptions&lt;NotificationOptions&gt; via Options.Create(...).
/// </summary>
public class NotificationDispatcherTests
{
    private static NotificationOptions EmptyOptions() => new();

    [Theory]
    [InlineData(null, null, false)]
    [InlineData("bot-token", null, false)]
    [InlineData(null, "chat-id", false)]
    [InlineData("", "", false)]
    [InlineData("bot-token", "chat-id", true)]
    public void TelegramChannel_IsConfigured_ReflectsTokenAndChatIdPresence(
        string? botToken, string? chatId, bool expected)
    {
        var options = EmptyOptions();
        options.TelegramBotToken = botToken;
        options.TelegramChatId = chatId;

        var channel = new TelegramChannel(
            Options.Create(options), NullLogger<TelegramChannel>.Instance);

        Assert.Equal(expected, channel.IsConfigured);
        Assert.Equal(NotificationChannelType.Telegram, channel.Type);
    }

    [Theory]
    [InlineData(null, null, false)]
    [InlineData("smtp.example.com", null, false)]
    [InlineData(null, "from@example.com", false)]
    [InlineData("", "", false)]
    [InlineData("smtp.example.com", "from@example.com", true)]
    public void EmailChannel_IsConfigured_ReflectsHostAndFromAddressPresence(
        string? smtpHost, string? fromAddress, bool expected)
    {
        var options = EmptyOptions();
        options.SmtpHost = smtpHost;
        options.SmtpFromAddress = fromAddress;

        var channel = new EmailChannel(
            Options.Create(options), NullLogger<EmailChannel>.Instance);

        Assert.Equal(expected, channel.IsConfigured);
        Assert.Equal(NotificationChannelType.Email, channel.Type);
    }

    // --- Task 2: NotificationDispatcher (graceful degradation + channel selection) ---

    [Fact]
    public async Task DispatchAsync_UnconfiguredChannel_IsNoOp_NeverThrows()
    {
        var telegram = new FakeNotificationChannel(NotificationChannelType.Telegram, isConfigured: false);
        var dispatcher = new NotificationDispatcher(
            [telegram], NullLogger<NotificationDispatcher>.Instance);

        await dispatcher.DispatchAsync(
            NotificationChannelType.Telegram, "title", "message", "user@example.com", CancellationToken.None);

        Assert.Equal(0, telegram.SendAsyncCallCount);
    }

    [Fact]
    public async Task DispatchAsync_Both_CallsEveryConfiguredChannel()
    {
        var telegram = new FakeNotificationChannel(NotificationChannelType.Telegram, isConfigured: true);
        var email = new FakeNotificationChannel(NotificationChannelType.Email, isConfigured: true);
        var dispatcher = new NotificationDispatcher(
            [telegram, email], NullLogger<NotificationDispatcher>.Instance);

        await dispatcher.DispatchAsync(
            NotificationChannelType.Both, "title", "message", "user@example.com", CancellationToken.None);

        Assert.Equal(1, telegram.SendAsyncCallCount);
        Assert.Equal(1, email.SendAsyncCallCount);
    }

    [Fact]
    public async Task DispatchAsync_Both_OneChannelThrows_OtherStillCalled_AndExceptionDoesNotPropagate()
    {
        var telegram = new FakeNotificationChannel(
            NotificationChannelType.Telegram, isConfigured: true, shouldThrow: true);
        var email = new FakeNotificationChannel(NotificationChannelType.Email, isConfigured: true);
        var dispatcher = new NotificationDispatcher(
            [telegram, email], NullLogger<NotificationDispatcher>.Instance);

        var exception = await Record.ExceptionAsync(() => dispatcher.DispatchAsync(
            NotificationChannelType.Both, "title", "message", "user@example.com", CancellationToken.None));

        Assert.Null(exception);
        Assert.Equal(1, telegram.SendAsyncCallCount);
        Assert.Equal(1, email.SendAsyncCallCount);
    }

    [Fact]
    public async Task DispatchAsync_Telegram_OnlyCallsTelegramChannel()
    {
        var telegram = new FakeNotificationChannel(NotificationChannelType.Telegram, isConfigured: true);
        var email = new FakeNotificationChannel(NotificationChannelType.Email, isConfigured: true);
        var dispatcher = new NotificationDispatcher(
            [telegram, email], NullLogger<NotificationDispatcher>.Instance);

        await dispatcher.DispatchAsync(
            NotificationChannelType.Telegram, "title", "message", "user@example.com", CancellationToken.None);

        Assert.Equal(1, telegram.SendAsyncCallCount);
        Assert.Equal(0, email.SendAsyncCallCount);
    }
}

/// <summary>
/// Test-only INotificationChannel double for NotificationDispatcherTests -- never instantiates
/// the real TelegramChannel/EmailChannel (no network I/O in unit tests), mirroring
/// tests/Sync/Fakes.cs's FakeFinancialDataProvider pattern.
/// </summary>
internal sealed class FakeNotificationChannel(
    NotificationChannelType type, bool isConfigured, bool shouldThrow = false) : INotificationChannel
{
    public NotificationChannelType Type => type;

    public bool IsConfigured => isConfigured;

    public int SendAsyncCallCount { get; private set; }

    public Task SendAsync(string title, string message, string recipientEmail, CancellationToken ct)
    {
        SendAsyncCallCount++;

        if (shouldThrow)
        {
            throw new InvalidOperationException("Simulated channel failure");
        }

        return Task.CompletedTask;
    }
}
