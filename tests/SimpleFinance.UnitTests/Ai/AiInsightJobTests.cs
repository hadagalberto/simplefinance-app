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
using SimpleFinance.Infrastructure.Persistence;

namespace SimpleFinance.UnitTests.Ai;

/// <summary>
/// Behavior tests for <see cref="AiInsightJob"/> (18-03 Task 2, AI-INS-01). Runs on the EF InMemory
/// provider with a fake detector + fake <see cref="ILlmClient"/>. Proves the nightly job only processes
/// users with <c>AiEnabled &amp;&amp; AiInsightsEnabled</c>, that running twice on identical data yields
/// exactly one <see cref="AiInsight"/> per DedupKey (idempotent via the service's dedup gate), and that an
/// over-budget run still persists insights with the deterministic fallback prose (graceful degrade).
/// </summary>
public class AiInsightJobTests
{
    private static readonly DateTime Now = DateTime.UtcNow;

    private static SimpleFinanceDbContext CreateDbContext() =>
        new(new DbContextOptionsBuilder<SimpleFinanceDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static InsightSignal Signal(string id, string fallbackTitle = "titulo padrao", string fallbackBody = "corpo padrao") =>
        new(
            id,
            AiInsightKind.SpendingChange,
            AiInsightSeverity.Info,
            "Alimentação",
            new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 7, 31, 23, 59, 59, DateTimeKind.Utc),
            "{\"currentAmount\":260}",
            id,
            fallbackTitle,
            fallbackBody);

    private static UserSettings Settings(Guid userId, bool aiEnabled, bool insightsEnabled) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        AiEnabled = aiEnabled,
        AiInsightsEnabled = insightsEnabled,
        AiChatModel = "deepseek-chat",
        CreatedAt = Now,
        UpdatedAt = Now,
    };

    private static AiInsightJob Build(
        SimpleFinanceDbContext db,
        IInsightDetector detector,
        ILlmClient llm)
    {
        var prose = new InsightProseWriter(llm, new PiiRedactor(), NullLogger<InsightProseWriter>.Instance);
        var service = new AiInsightService(db);
        return new AiInsightJob(db, detector, prose, service, NullLogger<AiInsightJob>.Instance);
    }

    [Fact]
    public async Task ExecuteAsync_OptedInUser_PersistsDetectedInsights()
    {
        await using var db = CreateDbContext();
        var userId = Guid.NewGuid();
        db.UserSettings.Add(Settings(userId, aiEnabled: true, insightsEnabled: true));
        await db.SaveChangesAsync();

        var detector = new FakeDetector([Signal("SpendingChange:food:2026-07")]);
        var llm = new FakeLlm { Content = "{\"items\":[{\"id\":\"SpendingChange:food:2026-07\",\"title\":\"Comida cara\",\"body\":\"Prose\"}]}" };

        await Build(db, detector, llm).ExecuteAsync(CancellationToken.None);

        var insight = Assert.Single(await db.AiInsights.ToListAsync());
        Assert.Equal(userId, insight.UserId);
        Assert.Equal(AiInsightStatus.New, insight.Status);
        Assert.Equal("Comida cara", insight.Title);
    }

    [Fact]
    public async Task ExecuteAsync_FlagsOff_NeverProcessesUser()
    {
        await using var db = CreateDbContext();
        var aiOffUser = Guid.NewGuid();
        var insightsOffUser = Guid.NewGuid();
        db.UserSettings.Add(Settings(aiOffUser, aiEnabled: false, insightsEnabled: true));
        db.UserSettings.Add(Settings(insightsOffUser, aiEnabled: true, insightsEnabled: false));
        await db.SaveChangesAsync();

        var detector = new FakeDetector([Signal("SpendingChange:food:2026-07")]);
        var llm = new FakeLlm();

        await Build(db, detector, llm).ExecuteAsync(CancellationToken.None);

        Assert.Empty(await db.AiInsights.ToListAsync());
        Assert.Equal(0, detector.CallCount);
    }

    [Fact]
    public async Task ExecuteAsync_RunTwiceOnSameData_OneRowPerDedupKey()
    {
        await using var db = CreateDbContext();
        var userId = Guid.NewGuid();
        db.UserSettings.Add(Settings(userId, aiEnabled: true, insightsEnabled: true));
        await db.SaveChangesAsync();

        var detector = new FakeDetector([Signal("SpendingChange:food:2026-07"), Signal("BudgetRisk:global:2026-07:80")]);
        var llm = new FakeLlm { Content = "{\"items\":[]}" };

        await Build(db, detector, llm).ExecuteAsync(CancellationToken.None);
        await Build(db, detector, llm).ExecuteAsync(CancellationToken.None);

        var rows = await db.AiInsights.ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.Equal(2, rows.Select(r => r.DedupKey).Distinct().Count());
    }

    [Fact]
    public async Task ExecuteAsync_OverBudget_StillPersistsWithFallbackProse()
    {
        await using var db = CreateDbContext();
        var userId = Guid.NewGuid();
        db.UserSettings.Add(Settings(userId, aiEnabled: true, insightsEnabled: true));
        await db.SaveChangesAsync();

        var detector = new FakeDetector([Signal("SpendingChange:food:2026-07", "fallback titulo", "fallback corpo")]);
        var llm = new FakeLlm { ThrowOnChat = new AiBudgetExceededException(userId, 5m, 5m) };

        await Build(db, detector, llm).ExecuteAsync(CancellationToken.None);

        var insight = Assert.Single(await db.AiInsights.ToListAsync());
        Assert.Equal("fallback titulo", insight.Title);
        Assert.Equal("fallback corpo", insight.Body);
        // Grounding: numbers survive an over-budget run.
        Assert.Equal("{\"currentAmount\":260}", insight.DataJson);
    }

    [Fact]
    public async Task ExecuteAsync_OneUserThrows_OtherUsersStillProcessed()
    {
        await using var db = CreateDbContext();
        var badUser = Guid.NewGuid();
        var goodUser = Guid.NewGuid();
        db.UserSettings.Add(Settings(badUser, aiEnabled: true, insightsEnabled: true));
        db.UserSettings.Add(Settings(goodUser, aiEnabled: true, insightsEnabled: true));
        await db.SaveChangesAsync();

        var detector = new ThrowingForUserDetector(badUser, [Signal("SpendingChange:food:2026-07")]);
        var llm = new FakeLlm { Content = "{\"items\":[]}" };

        await Build(db, detector, llm).ExecuteAsync(CancellationToken.None);

        var rows = await db.AiInsights.ToListAsync();
        var insight = Assert.Single(rows);
        Assert.Equal(goodUser, insight.UserId);
    }

    // ---- fakes ---------------------------------------------------------------------------------

    private sealed class FakeDetector(IReadOnlyList<InsightSignal> signals) : IInsightDetector
    {
        public int CallCount { get; private set; }

        public Task<IReadOnlyList<InsightSignal>> DetectAsync(Guid userId, DateTime referenceDate, CancellationToken ct)
        {
            CallCount++;
            return Task.FromResult(signals);
        }
    }

    private sealed class ThrowingForUserDetector(Guid throwFor, IReadOnlyList<InsightSignal> signals) : IInsightDetector
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
