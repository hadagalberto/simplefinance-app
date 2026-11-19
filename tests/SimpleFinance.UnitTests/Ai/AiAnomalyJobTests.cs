using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SimpleFinance.Application.Ai;
using SimpleFinance.Application.Ai.Usage;
using SimpleFinance.Domain.Entities;
using SimpleFinance.Domain.Enums;
using SimpleFinance.Infrastructure.Ai;
using SimpleFinance.Infrastructure.Ai.Insights;
using SimpleFinance.Infrastructure.Jobs;
using SimpleFinance.Infrastructure.Notifications;
using SimpleFinance.Infrastructure.Persistence;

namespace SimpleFinance.UnitTests.Ai;

/// <summary>
/// Behavior tests for <see cref="AiAnomalyJob"/> (18-05 Task 1, AI-INS-02/03). Runs on the EF InMemory
/// provider with a fake detector + fake <see cref="ILlmClient"/> and a COUNTING
/// <see cref="INotificationChannel"/> inside a real <see cref="NotificationDispatcher"/> (the AiBudgetGuard
/// precedent) so a dispatch is provable by call count. Proves the fire-once dispatch rule: high-severity
/// z-score/duplicate anomalies push exactly once (a second run never re-dispatches — the unique
/// (UserId, DedupKey) index is the guarantee), subscription-increase + atypical-category are CARD-ONLY
/// (persist, never push), a dismissed occurrence never re-fires, and an over-budget run still persists +
/// still dispatches (dispatch is independent of the LLM).
/// </summary>
public class AiAnomalyJobTests
{
    private static readonly DateTime Now = DateTime.UtcNow;

    private static SimpleFinanceDbContext CreateDbContext() =>
        new(new DbContextOptionsBuilder<SimpleFinanceDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    // A Kind=Anomaly signal whose DedupKey prefix decides dispatch-eligibility (zscore/dup dispatch;
    // subincrease/category are card-only). Mirrors the AnomalyDetector's exact DedupKey namespaces.
    private static InsightSignal Anomaly(string dedupKey, AiInsightSeverity severity) =>
        new(
            dedupKey,
            AiInsightKind.Anomaly,
            severity,
            "Mercado",
            new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 7, 31, 23, 59, 59, DateTimeKind.Utc),
            "{\"subkind\":\"zscore\",\"amount\":400}",
            dedupKey,
            "Gasto atípico",
            "Uma cobrança muito acima do padrão.");

    private static UserSettings Settings(Guid userId, bool aiEnabled, bool anomalyEnabled) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        AiEnabled = aiEnabled,
        AiAnomalyEnabled = anomalyEnabled,
        AiChatModel = "deepseek-chat",
        CreatedAt = Now,
        UpdatedAt = Now,
    };

    private static User AppUser(Guid userId) => new()
    {
        Id = userId,
        Email = "owner@example.com",
        PasswordHash = "x",
        CreatedAt = Now,
        UpdatedAt = Now,
    };

    private static (AiAnomalyJob Job, CountingChannel Channel) Build(
        SimpleFinanceDbContext db,
        IAnomalyDetector detector,
        ILlmClient llm)
    {
        var prose = new InsightProseWriter(llm, new PiiRedactor(), NullLogger<InsightProseWriter>.Instance);
        var service = new AiInsightService(db);
        var channel = new CountingChannel();
        var dispatcher = new NotificationDispatcher([channel], NullLogger<NotificationDispatcher>.Instance);
        var job = new AiAnomalyJob(db, detector, prose, service, dispatcher, NullLogger<AiAnomalyJob>.Instance);
        return (job, channel);
    }

    [Fact]
    public async Task ExecuteAsync_ZScoreWarning_DispatchesOnce_SecondRunNeverRedispatches()
    {
        await using var db = CreateDbContext();
        var userId = Guid.NewGuid();
        db.UserSettings.Add(Settings(userId, aiEnabled: true, anomalyEnabled: true));
        db.Users.Add(AppUser(userId));
        await db.SaveChangesAsync();

        var detector = new FakeAnomalyDetector([Anomaly("Anomaly:zscore:txn-1", AiInsightSeverity.Warning)]);
        var llm = new FakeLlm { Content = "{\"items\":[]}" };

        var (job, channel) = Build(db, detector, llm);
        await job.ExecuteAsync(CancellationToken.None);

        var insight = Assert.Single(await db.AiInsights.ToListAsync());
        Assert.Equal(AiInsightKind.Anomaly, insight.Kind);
        var notification = Assert.Single(await db.Notifications.ToListAsync());
        Assert.Equal(AlertRuleType.AiAnomaly, notification.Type);
        Assert.Equal(NotificationChannelType.Both, notification.Channel);
        Assert.Equal(1, channel.SendAsyncCallCount);

        // Second run over the SAME data: Upsert returns false (already exists) → no new insight, no re-dispatch.
        var (job2, channel2) = Build(db, detector, llm);
        await job2.ExecuteAsync(CancellationToken.None);

        Assert.Single(await db.AiInsights.ToListAsync());
        Assert.Single(await db.Notifications.ToListAsync());
        Assert.Equal(0, channel2.SendAsyncCallCount);
    }

    [Fact]
    public async Task ExecuteAsync_DuplicateWarning_Dispatches()
    {
        await using var db = CreateDbContext();
        var userId = Guid.NewGuid();
        db.UserSettings.Add(Settings(userId, aiEnabled: true, anomalyEnabled: true));
        db.Users.Add(AppUser(userId));
        await db.SaveChangesAsync();

        var detector = new FakeAnomalyDetector([Anomaly("Anomaly:dup:txn-2", AiInsightSeverity.Critical)]);
        var llm = new FakeLlm { Content = "{\"items\":[]}" };

        var (job, channel) = Build(db, detector, llm);
        await job.ExecuteAsync(CancellationToken.None);

        Assert.Single(await db.Notifications.ToListAsync());
        Assert.Equal(1, channel.SendAsyncCallCount);
    }

    [Fact]
    public async Task ExecuteAsync_SubscriptionIncrease_CardOnly_NeverDispatches()
    {
        await using var db = CreateDbContext();
        var userId = Guid.NewGuid();
        db.UserSettings.Add(Settings(userId, aiEnabled: true, anomalyEnabled: true));
        db.Users.Add(AppUser(userId));
        await db.SaveChangesAsync();

        var detector = new FakeAnomalyDetector([Anomaly("Anomaly:subincrease:rec-1:35.00", AiInsightSeverity.Critical)]);
        var llm = new FakeLlm { Content = "{\"items\":[]}" };

        var (job, channel) = Build(db, detector, llm);
        await job.ExecuteAsync(CancellationToken.None);

        // Persisted as a card, but NEVER pushed (v1 alerts already own its push).
        Assert.Single(await db.AiInsights.ToListAsync());
        Assert.Empty(await db.Notifications.ToListAsync());
        Assert.Equal(0, channel.SendAsyncCallCount);
    }

    [Fact]
    public async Task ExecuteAsync_AtypicalCategory_CardOnly_NeverDispatches()
    {
        await using var db = CreateDbContext();
        var userId = Guid.NewGuid();
        db.UserSettings.Add(Settings(userId, aiEnabled: true, anomalyEnabled: true));
        db.Users.Add(AppUser(userId));
        await db.SaveChangesAsync();

        var detector = new FakeAnomalyDetector([Anomaly("Anomaly:category:cat-1:2026-07", AiInsightSeverity.Critical)]);
        var llm = new FakeLlm { Content = "{\"items\":[]}" };

        var (job, channel) = Build(db, detector, llm);
        await job.ExecuteAsync(CancellationToken.None);

        Assert.Single(await db.AiInsights.ToListAsync());
        Assert.Empty(await db.Notifications.ToListAsync());
        Assert.Equal(0, channel.SendAsyncCallCount);
    }

    [Fact]
    public async Task ExecuteAsync_ZScoreBelowWarning_CardOnly_NeverDispatches()
    {
        await using var db = CreateDbContext();
        var userId = Guid.NewGuid();
        db.UserSettings.Add(Settings(userId, aiEnabled: true, anomalyEnabled: true));
        db.Users.Add(AppUser(userId));
        await db.SaveChangesAsync();

        // Info severity is below the Warning dispatch gate even for a dispatch-eligible subkind.
        var detector = new FakeAnomalyDetector([Anomaly("Anomaly:zscore:txn-3", AiInsightSeverity.Info)]);
        var llm = new FakeLlm { Content = "{\"items\":[]}" };

        var (job, channel) = Build(db, detector, llm);
        await job.ExecuteAsync(CancellationToken.None);

        Assert.Single(await db.AiInsights.ToListAsync());
        Assert.Empty(await db.Notifications.ToListAsync());
        Assert.Equal(0, channel.SendAsyncCallCount);
    }

    [Fact]
    public async Task ExecuteAsync_OverBudget_StillPersistsAndStillDispatches()
    {
        await using var db = CreateDbContext();
        var userId = Guid.NewGuid();
        db.UserSettings.Add(Settings(userId, aiEnabled: true, anomalyEnabled: true));
        db.Users.Add(AppUser(userId));
        await db.SaveChangesAsync();

        var detector = new FakeAnomalyDetector([Anomaly("Anomaly:zscore:txn-4", AiInsightSeverity.Warning)]);
        var llm = new FakeLlm { ThrowOnChat = new AiBudgetExceededException(userId, 5m, 5m) };

        var (job, channel) = Build(db, detector, llm);
        await job.ExecuteAsync(CancellationToken.None);

        // Fallback prose persisted; dispatch is independent of the LLM so it still fires.
        var insight = Assert.Single(await db.AiInsights.ToListAsync());
        Assert.Equal("Gasto atípico", insight.Title);
        Assert.Single(await db.Notifications.ToListAsync());
        Assert.Equal(1, channel.SendAsyncCallCount);
    }

    [Fact]
    public async Task ExecuteAsync_DismissedOccurrence_NeverRefires_And_NeverDispatches()
    {
        await using var db = CreateDbContext();
        var userId = Guid.NewGuid();
        db.UserSettings.Add(Settings(userId, aiEnabled: true, anomalyEnabled: true));
        db.Users.Add(AppUser(userId));
        // Pre-seed a Dismissed insight with the SAME DedupKey the detector will surface.
        db.AiInsights.Add(new AiInsight
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Kind = AiInsightKind.Anomaly,
            Severity = AiInsightSeverity.Warning,
            Title = "old",
            Body = "old",
            DataJson = "{}",
            DedupKey = "Anomaly:zscore:txn-5",
            Status = AiInsightStatus.Dismissed,
            CreatedAt = Now,
            UpdatedAt = Now,
        });
        await db.SaveChangesAsync();

        var detector = new FakeAnomalyDetector([Anomaly("Anomaly:zscore:txn-5", AiInsightSeverity.Warning)]);
        var llm = new FakeLlm { Content = "{\"items\":[]}" };

        var (job, channel) = Build(db, detector, llm);
        await job.ExecuteAsync(CancellationToken.None);

        // The dismissed row still blocks re-creation; nothing new persists and nothing dispatches.
        var rows = await db.AiInsights.ToListAsync();
        Assert.Single(rows);
        Assert.Equal(AiInsightStatus.Dismissed, rows[0].Status);
        Assert.Empty(await db.Notifications.ToListAsync());
        Assert.Equal(0, channel.SendAsyncCallCount);
    }

    [Fact]
    public async Task ExecuteAsync_FlagsOff_NeverProcessesUser()
    {
        await using var db = CreateDbContext();
        var aiOff = Guid.NewGuid();
        var anomalyOff = Guid.NewGuid();
        db.UserSettings.Add(Settings(aiOff, aiEnabled: false, anomalyEnabled: true));
        db.UserSettings.Add(Settings(anomalyOff, aiEnabled: true, anomalyEnabled: false));
        db.Users.Add(AppUser(aiOff));
        db.Users.Add(AppUser(anomalyOff));
        await db.SaveChangesAsync();

        var detector = new FakeAnomalyDetector([Anomaly("Anomaly:zscore:txn-6", AiInsightSeverity.Critical)]);
        var llm = new FakeLlm();

        var (job, channel) = Build(db, detector, llm);
        await job.ExecuteAsync(CancellationToken.None);

        Assert.Empty(await db.AiInsights.ToListAsync());
        Assert.Empty(await db.Notifications.ToListAsync());
        Assert.Equal(0, detector.CallCount);
        Assert.Equal(0, channel.SendAsyncCallCount);
    }

    [Fact]
    public async Task ExecuteAsync_OneUserThrows_OtherUsersStillProcessed()
    {
        await using var db = CreateDbContext();
        var badUser = Guid.NewGuid();
        var goodUser = Guid.NewGuid();
        db.UserSettings.Add(Settings(badUser, aiEnabled: true, anomalyEnabled: true));
        db.UserSettings.Add(Settings(goodUser, aiEnabled: true, anomalyEnabled: true));
        db.Users.Add(AppUser(badUser));
        db.Users.Add(AppUser(goodUser));
        await db.SaveChangesAsync();

        var detector = new ThrowingForUserAnomalyDetector(badUser, [Anomaly("Anomaly:zscore:txn-7", AiInsightSeverity.Warning)]);
        var llm = new FakeLlm { Content = "{\"items\":[]}" };

        var (job, _) = Build(db, detector, llm);
        await job.ExecuteAsync(CancellationToken.None);

        var insight = Assert.Single(await db.AiInsights.ToListAsync());
        Assert.Equal(goodUser, insight.UserId);
    }

    // ---- fakes ---------------------------------------------------------------------------------

    private sealed class FakeAnomalyDetector(IReadOnlyList<InsightSignal> signals) : IAnomalyDetector
    {
        public int CallCount { get; private set; }

        public Task<IReadOnlyList<InsightSignal>> DetectAsync(Guid userId, DateTime referenceDate, CancellationToken ct)
        {
            CallCount++;
            return Task.FromResult(signals);
        }
    }

    private sealed class ThrowingForUserAnomalyDetector(Guid throwFor, IReadOnlyList<InsightSignal> signals) : IAnomalyDetector
    {
        public Task<IReadOnlyList<InsightSignal>> DetectAsync(Guid userId, DateTime referenceDate, CancellationToken ct)
        {
            if (userId == throwFor)
            {
                throw new InvalidOperationException("boom");
            }

            return Task.FromResult(signals);
        }
    }

    private sealed class CountingChannel : INotificationChannel
    {
        public NotificationChannelType Type => NotificationChannelType.Telegram;

        public bool IsConfigured => true;

        public int SendAsyncCallCount { get; private set; }

        public Task SendAsync(string title, string message, string recipientEmail, CancellationToken ct)
        {
            SendAsyncCallCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeLlm : ILlmClient
    {
        public string Content { get; set; } = "{\"items\":[]}";

        public Exception? ThrowOnChat { get; set; }

        public Task<LlmResult> ChatAsync(LlmChatRequest request, CancellationToken ct)
        {
            if (ThrowOnChat is not null)
            {
                throw ThrowOnChat;
            }

            return Task.FromResult(new LlmResult(Content, null, [], new TokenUsage(10, 5)));
        }

        public async IAsyncEnumerable<LlmStreamChunk> ChatStreamAsync(
            LlmChatRequest request,
            [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            throw new NotSupportedException();
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }
    }
}
