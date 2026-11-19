using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SimpleFinance.Application.Ai;
using SimpleFinance.Application.Ai.Usage;
using SimpleFinance.Domain.Entities;
using SimpleFinance.Infrastructure.Ai.Forecast;
using SimpleFinance.Infrastructure.Jobs;
using SimpleFinance.Infrastructure.Persistence;
using SimpleFinance.Infrastructure.Reporting;

namespace SimpleFinance.UnitTests.Jobs;

/// <summary>
/// Behavior tests for <see cref="AiForecastJob"/> (19-02, AI-FCT-01/02). Runs on the EF InMemory provider
/// with fake reader bridges (realized totals + trailing history) and a fake <see cref="ILlmClient"/>. Proves
/// the daily job only processes users with <c>AiEnabled &amp;&amp; AiForecastEnabled</c>, that a successful
/// LLM call persists the narrative alongside the deterministic numbers, that an over-budget run still
/// persists the forecast with a null narrative (AI-FCT-02 degrade), and that one user throwing does not
/// abort the batch.
/// </summary>
public class AiForecastJobTests
{
    private static readonly DateTime Now = DateTime.UtcNow;

    private static SimpleFinanceDbContext CreateDbContext() =>
        new(new DbContextOptionsBuilder<SimpleFinanceDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static UserSettings Settings(Guid userId, bool aiEnabled, bool forecastEnabled) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        AiEnabled = aiEnabled,
        AiForecastEnabled = forecastEnabled,
        AiChatModel = "deepseek-chat",
        CreatedAt = Now,
        UpdatedAt = Now,
    };

    private static AiForecastJob Build(SimpleFinanceDbContext db, ILlmClient llm, IPeriodTotalsReader? totals = null)
    {
        var calculator = new ForecastCalculator(totals ?? DefaultTotals(), new FakeCategorySpendReader(), db);
        var narrator = new ForecastNarrativeWriter(llm, NullLogger<ForecastNarrativeWriter>.Instance);
        var service = new ForecastService(db);
        return new AiForecastJob(db, calculator, narrator, service, NullLogger<AiForecastJob>.Instance);
    }

    private static FakePeriodTotalsReader DefaultTotals()
    {
        var totals = new FakePeriodTotalsReader();
        var thisMonth = new DateTime(Now.Year, Now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        totals.Set(thisMonth.ToString("yyyy-MM"), income: 8000m, expense: 2000m);
        for (var i = 1; i <= 3; i++)
        {
            totals.Set(thisMonth.AddMonths(-i).ToString("yyyy-MM"), income: 8000m, expense: 3000m);
        }
        return totals;
    }

    [Fact]
    public async Task ExecuteAsync_OptedInUser_PersistsForecastWithNarrativeAndDeterministicNumbers()
    {
        await using var db = CreateDbContext();
        var userId = Guid.NewGuid();
        db.UserSettings.Add(Settings(userId, aiEnabled: true, forecastEnabled: true));
        await db.SaveChangesAsync();

        var llm = new FakeLlm { Content = "No ritmo atual, você fecha o mês positivo." };

        await Build(db, llm).ExecuteAsync(CancellationToken.None);

        var forecast = Assert.Single(await db.Forecasts.ToListAsync());
        Assert.Equal(userId, forecast.UserId);
        Assert.Equal("No ritmo atual, você fecha o mês positivo.", forecast.Narrative);
        Assert.Equal("recurring+3mo-avg", forecast.Method);
        // Deterministic numbers survive; balance = income − expense.
        Assert.Equal(forecast.ProjectedIncome - forecast.ProjectedExpense, forecast.ProjectedBalance);
        Assert.True(forecast.ProjectedExpense > 0m);
    }

    [Fact]
    public async Task ExecuteAsync_FlagsOff_NeverProcessesUser()
    {
        await using var db = CreateDbContext();
        var aiOffUser = Guid.NewGuid();
        var forecastOffUser = Guid.NewGuid();
        db.UserSettings.Add(Settings(aiOffUser, aiEnabled: false, forecastEnabled: true));
        db.UserSettings.Add(Settings(forecastOffUser, aiEnabled: true, forecastEnabled: false));
        await db.SaveChangesAsync();

        var llm = new FakeLlm();

        await Build(db, llm).ExecuteAsync(CancellationToken.None);

        Assert.Empty(await db.Forecasts.ToListAsync());
        Assert.Equal(0, llm.CallCount);
    }

    [Fact]
    public async Task ExecuteAsync_OverBudget_StillPersistsForecastWithNullNarrative()
    {
        await using var db = CreateDbContext();
        var userId = Guid.NewGuid();
        db.UserSettings.Add(Settings(userId, aiEnabled: true, forecastEnabled: true));
        await db.SaveChangesAsync();

        var llm = new FakeLlm { ThrowOnChat = new AiBudgetExceededException(userId, 5m, 5m) };

        await Build(db, llm).ExecuteAsync(CancellationToken.None);

        var forecast = Assert.Single(await db.Forecasts.ToListAsync());
        Assert.Null(forecast.Narrative);
        // AI-FCT-02 grounding: the deterministic projection persists even when the LLM is unavailable.
        Assert.True(forecast.ProjectedExpense > 0m);
        Assert.Equal(forecast.ProjectedIncome - forecast.ProjectedExpense, forecast.ProjectedBalance);
    }

    [Fact]
    public async Task ExecuteAsync_RunTwiceSameMonth_ReplacesSingleRow()
    {
        await using var db = CreateDbContext();
        var userId = Guid.NewGuid();
        db.UserSettings.Add(Settings(userId, aiEnabled: true, forecastEnabled: true));
        await db.SaveChangesAsync();

        var llm = new FakeLlm { Content = "Projeção." };

        await Build(db, llm).ExecuteAsync(CancellationToken.None);
        await Build(db, llm).ExecuteAsync(CancellationToken.None);

        // REPLACE-per-month: exactly one row for the (UserId, Month) pair after two runs.
        Assert.Single(await db.Forecasts.ToListAsync());
    }

    [Fact]
    public async Task ExecuteAsync_OneUserThrows_OtherUsersStillProcessed()
    {
        await using var db = CreateDbContext();
        var badUser = Guid.NewGuid();
        var goodUser = Guid.NewGuid();
        db.UserSettings.Add(Settings(badUser, aiEnabled: true, forecastEnabled: true));
        db.UserSettings.Add(Settings(goodUser, aiEnabled: true, forecastEnabled: true));
        await db.SaveChangesAsync();

        var llm = new FakeLlm { Content = "Projeção." };
        var totals = new ThrowingForUserTotalsReader(badUser, DefaultTotals());

        await Build(db, llm, totals).ExecuteAsync(CancellationToken.None);

        var forecast = Assert.Single(await db.Forecasts.ToListAsync());
        Assert.Equal(goodUser, forecast.UserId);
    }

    // ---- fakes ---------------------------------------------------------------------------------

    private sealed class FakePeriodTotalsReader : IPeriodTotalsReader
    {
        private readonly Dictionary<string, DashboardTotals> _byMonth = new();

        public void Set(string yyyyMM, decimal income, decimal expense) =>
            _byMonth[yyyyMM] = new DashboardTotals(0m, 0m, 0m, income, expense);

        public Task<DashboardTotals> GetTotalsAsync(Guid userId, DateTime periodStart, DateTime periodEnd, CancellationToken ct)
        {
            var key = periodStart.ToString("yyyy-MM");
            var totals = _byMonth.TryGetValue(key, out var t) ? t : new DashboardTotals(0m, 0m, 0m, 0m, 0m);
            return Task.FromResult(totals);
        }
    }

    private sealed class ThrowingForUserTotalsReader(Guid throwFor, IPeriodTotalsReader inner) : IPeriodTotalsReader
    {
        public Task<DashboardTotals> GetTotalsAsync(Guid userId, DateTime periodStart, DateTime periodEnd, CancellationToken ct)
        {
            if (userId == throwFor)
            {
                throw new InvalidOperationException("boom");
            }

            return inner.GetTotalsAsync(userId, periodStart, periodEnd, ct);
        }
    }

    private sealed class FakeCategorySpendReader : ICategorySpendReader
    {
        public Task<IReadOnlyList<CategorySpend>> GetCategorySpendAsync(Guid userId, DateTime periodStart, DateTime periodEnd, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<CategorySpend>>([]);
    }

    private sealed class FakeLlm : ILlmClient
    {
        public string Content { get; set; } = "Projeção.";

        public Exception? ThrowOnChat { get; set; }

        public int CallCount { get; private set; }

        public Task<LlmResult> ChatAsync(LlmChatRequest request, CancellationToken ct)
        {
            CallCount++;
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
