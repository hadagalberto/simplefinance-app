using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SimpleFinance.Application.Ai.Usage;
using SimpleFinance.Domain.Entities;
using SimpleFinance.Domain.Enums;
using SimpleFinance.Infrastructure.Ai.Usage;
using SimpleFinance.Infrastructure.Notifications;
using SimpleFinance.Infrastructure.Persistence;

namespace SimpleFinance.UnitTests.Ai;

/// <summary>
/// AI-FND-05 (15-04): the budget kill-switch. Verified against EF Core's InMemory provider with an
/// inspectable notification channel (mirrors AiUsageRecorderTests' InMemory pattern) — no real DB or
/// network required. Covers: cap=0 blocks the first call (Denial-of-Wallet, T-15-07), below-cap
/// passes, crossing the cap throws the typed <see cref="AiBudgetExceededException"/>, exactly one
/// notification fires per month (dedup, T-15-09), and the sum is scoped to the current calendar
/// month so the budget resets naturally on rollover.
/// </summary>
public class AiBudgetGuardTests
{
    private static SimpleFinanceDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<SimpleFinanceDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options);

    /// <summary>Inspectable channel: counts SendAsync calls so tests can assert dispatch dedup.</summary>
    private sealed class CountingChannel : INotificationChannel
    {
        public int SendCount { get; private set; }

        public NotificationChannelType Type => NotificationChannelType.Telegram;

        public bool IsConfigured => true;

        public Task SendAsync(string title, string message, string recipientEmail, CancellationToken ct)
        {
            SendCount++;
            return Task.CompletedTask;
        }
    }

    private static NotificationDispatcher CreateDispatcher(CountingChannel channel) =>
        new(new INotificationChannel[] { channel }, NullLogger<NotificationDispatcher>.Instance);

    private static async Task SeedSettingsAsync(SimpleFinanceDbContext db, Guid userId, decimal budgetUsd)
    {
        var now = DateTime.UtcNow;
        db.UserSettings.Add(new UserSettings
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            AiMonthlyBudgetUsd = budgetUsd,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync();
    }

    private static async Task SeedUsageAsync(SimpleFinanceDbContext db, Guid userId, decimal costUsd, DateTime createdAtUtc)
    {
        db.AiUsageLogs.Add(new AiUsageLog
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Feature = AiFeature.Categorization,
            Provider = AiProvider.DeepSeek,
            Model = "deepseek-chat",
            PromptTokens = 100,
            CompletionTokens = 50,
            EstimatedCostUsd = costUsd,
            CreatedAt = createdAtUtc,
            UpdatedAt = createdAtUtc,
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task EnsureWithinBudget_CapZero_ThrowsOnFirstCall_WithZeroPriorSpend()
    {
        var userId = Guid.NewGuid();
        await using var db = CreateDb();
        await SeedSettingsAsync(db, userId, budgetUsd: 0m);
        var channel = new CountingChannel();
        var guard = new AiBudgetGuard(db, CreateDispatcher(channel));

        var ex = await Assert.ThrowsAsync<AiBudgetExceededException>(
            () => guard.EnsureWithinBudgetAsync(userId, CancellationToken.None));

        Assert.Equal(userId, ex.UserId);
        Assert.Equal(0m, ex.MonthlyBudgetUsd);
        Assert.Equal(0m, ex.CurrentSpendUsd);
        Assert.Equal(1, channel.SendCount);
    }

    [Fact]
    public async Task EnsureWithinBudget_SpendBelowCap_ReturnsWithoutThrowing_AndDoesNotNotify()
    {
        var userId = Guid.NewGuid();
        await using var db = CreateDb();
        await SeedSettingsAsync(db, userId, budgetUsd: 3.00m);
        await SeedUsageAsync(db, userId, costUsd: 1.25m, DateTime.UtcNow);
        var channel = new CountingChannel();
        var guard = new AiBudgetGuard(db, CreateDispatcher(channel));

        await guard.EnsureWithinBudgetAsync(userId, CancellationToken.None);

        Assert.Equal(0, channel.SendCount);
        Assert.Empty(await db.Notifications.ToListAsync());
    }

    [Fact]
    public async Task EnsureWithinBudget_SpendCrossesCap_Throws()
    {
        var userId = Guid.NewGuid();
        await using var db = CreateDb();
        await SeedSettingsAsync(db, userId, budgetUsd: 1.00m);
        await SeedUsageAsync(db, userId, costUsd: 0.80m, DateTime.UtcNow);
        await SeedUsageAsync(db, userId, costUsd: 0.70m, DateTime.UtcNow);
        var channel = new CountingChannel();
        var guard = new AiBudgetGuard(db, CreateDispatcher(channel));

        var ex = await Assert.ThrowsAsync<AiBudgetExceededException>(
            () => guard.EnsureWithinBudgetAsync(userId, CancellationToken.None));

        Assert.Equal(1.00m, ex.MonthlyBudgetUsd);
        Assert.Equal(1.50m, ex.CurrentSpendUsd);
    }

    [Fact]
    public async Task EnsureWithinBudget_OverBudgetTwiceSameMonth_DispatchesExactlyOnce()
    {
        var userId = Guid.NewGuid();
        await using var db = CreateDb();
        await SeedSettingsAsync(db, userId, budgetUsd: 0m);
        var channel = new CountingChannel();
        var guard = new AiBudgetGuard(db, CreateDispatcher(channel));

        await Assert.ThrowsAsync<AiBudgetExceededException>(
            () => guard.EnsureWithinBudgetAsync(userId, CancellationToken.None));
        await Assert.ThrowsAsync<AiBudgetExceededException>(
            () => guard.EnsureWithinBudgetAsync(userId, CancellationToken.None));

        Assert.Equal(1, channel.SendCount);
        Assert.Single(await db.Notifications.ToListAsync());
    }

    [Fact]
    public async Task EnsureWithinBudget_IgnoresPriorMonthSpend_ResetsOnRollover()
    {
        var userId = Guid.NewGuid();
        await using var db = CreateDb();
        await SeedSettingsAsync(db, userId, budgetUsd: 3.00m);

        // A large charge from last month must not count against this month's cap.
        var now = DateTime.UtcNow;
        var thisMonthStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        await SeedUsageAsync(db, userId, costUsd: 100m, thisMonthStart.AddDays(-1));

        var channel = new CountingChannel();
        var guard = new AiBudgetGuard(db, CreateDispatcher(channel));

        await guard.EnsureWithinBudgetAsync(userId, CancellationToken.None);

        Assert.Equal(0, channel.SendCount);
    }
}
