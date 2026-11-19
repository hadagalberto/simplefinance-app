using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SimpleFinance.Domain.Entities;
using SimpleFinance.Domain.Enums;
using SimpleFinance.Infrastructure.Ai.Assistant;
using SimpleFinance.Infrastructure.Ai.Assistant.Tools;
using SimpleFinance.Infrastructure.Budgets;
using SimpleFinance.Infrastructure.Goals;
using SimpleFinance.Infrastructure.Persistence;
using SimpleFinance.Infrastructure.Reporting;

namespace SimpleFinance.UnitTests.Assistant;

/// <summary>
/// AI-CHAT-04 — the SAFETY-SPINE behavior tests (17-06 Task 1). Proves the hard invariant: the four
/// write tools only ever PROPOSE (stage an AiPendingAction(Pending)) and MUTATE NO domain table; nothing
/// is written until AiPendingActionService.ConfirmAsync executes exactly once by dispatching to the
/// existing domain service; a re-confirm is a Conflict with no double-apply; an expired action is
/// rejected without a write; cancel discards without a write; every op is UserId-scoped; and a
/// hallucinated category name is rejected before anything is staged.
///
/// Confirm-execution is exercised through CreateGoal/CreateRule/Recategorize (pure-EF domain writes that
/// run on the InMemory provider). SetBudget's confirm goes through BudgetService.UpsertBudgetAsync, whose
/// derived-row computation reads a Dapper aggregate that needs real Postgres — so SetBudget is proven at
/// the propose (staging) + unknown-name-rejection level here, and its confirm dispatch is a direct call
/// to that already-tested service.
/// </summary>
public class PendingActionTests
{
    private static SimpleFinanceDbContext CreateDbContext() =>
        new(new DbContextOptionsBuilder<SimpleFinanceDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    // BudgetService needs a DashboardQueries (Dapper) that is never invoked on the CreateGoal/CreateRule/
    // Recategorize paths — passing a factory-less instance is safe because those branches never call it.
    private static AiPendingActionService CreateService(SimpleFinanceDbContext db)
    {
        var executor = new PendingActionExecutor(db, new GoalService(db), new BudgetService(db, new DashboardQueries(null!)));
        return new AiPendingActionService(db, executor);
    }

    private static async Task<Category> SeedCategoryAsync(SimpleFinanceDbContext db, Guid userId, string name)
    {
        var category = new Category { Id = Guid.NewGuid(), UserId = userId, Name = name, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        db.Categories.Add(category);
        await db.SaveChangesAsync();
        return category;
    }

    // ---- propose does NOT mutate (T-17-18) ----------------------------------------------------

    [Fact]
    public async Task CreateGoal_Propose_StagesOnePendingAction_MutatesNoDomainTable()
    {
        await using var db = CreateDbContext();
        var userId = Guid.NewGuid();
        var service = CreateService(db);
        var tool = new CreateGoalTool(service);

        var result = await tool.ExecuteAsync("""{"name":"Viagem","targetAmount":5000,"targetDate":"2026-12-31"}""", userId, CancellationToken.None);

        // exactly one Pending action staged, zero goals written
        var actions = await db.AiPendingActions.ToListAsync();
        Assert.Single(actions);
        Assert.Equal(PendingActionStatus.Pending, actions[0].Status);
        Assert.Equal(PendingActionType.CreateGoal, actions[0].ActionType);
        Assert.Null(actions[0].DecidedAt);
        Assert.Equal(0, await db.Goals.CountAsync());

        // the tool result carries the staged action id for the confirmation card
        using var doc = JsonDocument.Parse(result);
        Assert.Equal(actions[0].Id, Guid.Parse(doc.RootElement.GetProperty("action").GetProperty("id").GetString()!));
        Assert.Equal("create_goal", doc.RootElement.GetProperty("action").GetProperty("actionType").GetString());
    }

    [Fact]
    public async Task SetBudget_Propose_StagesOnePendingAction_MutatesNoBudget()
    {
        await using var db = CreateDbContext();
        var userId = Guid.NewGuid();
        await SeedCategoryAsync(db, userId, "Alimentação");
        var service = CreateService(db);
        var tool = new SetBudgetTool(db, service);

        await tool.ExecuteAsync("""{"categoryName":"Alimentação","month":"2026-05","amount":1200}""", userId, CancellationToken.None);

        Assert.Single(await db.AiPendingActions.ToListAsync());
        Assert.Equal(0, await db.Budgets.CountAsync());
    }

    [Fact]
    public async Task CreateCategoryRule_Propose_StagesOnePendingAction_NoRuleCreated()
    {
        await using var db = CreateDbContext();
        var userId = Guid.NewGuid();
        await SeedCategoryAsync(db, userId, "Transporte");
        var service = CreateService(db);
        var tool = new CreateCategoryRuleTool(db, service);

        await tool.ExecuteAsync("""{"pattern":"UBER","matchType":"contains","categoryName":"Transporte"}""", userId, CancellationToken.None);

        Assert.Single(await db.AiPendingActions.ToListAsync());
        Assert.Equal(0, await db.CategoryRules.CountAsync());
    }

    [Fact]
    public async Task Recategorize_Propose_StagesOnePendingAction_NoTransactionChanged()
    {
        await using var db = CreateDbContext();
        var userId = Guid.NewGuid();
        await SeedCategoryAsync(db, userId, "Transporte");
        var txn = await SeedTransactionAsync(db, userId, "UBER TRIP", 30m, categoryId: null, CategorySource.SystemMapping);
        var service = CreateService(db);
        var tool = new RecategorizeTransactionsTool(db, service);

        await tool.ExecuteAsync("""{"filter":{"merchant":"uber"},"toCategoryName":"Transporte","maxCount":50}""", userId, CancellationToken.None);

        Assert.Single(await db.AiPendingActions.ToListAsync());
        var reloaded = await db.Transactions.FirstAsync(t => t.Id == txn.Id);
        Assert.Null(reloaded.CategoryId); // untouched — nothing executed at propose time
    }

    // ---- unknown / hallucinated category rejected before staging (T-17-21) --------------------

    [Fact]
    public async Task CreateCategoryRule_UnknownCategory_ReturnsError_NoActionStaged()
    {
        await using var db = CreateDbContext();
        var userId = Guid.NewGuid();
        var service = CreateService(db);
        var tool = new CreateCategoryRuleTool(db, service);

        var result = await tool.ExecuteAsync("""{"pattern":"UBER","matchType":"contains","categoryName":"NãoExiste"}""", userId, CancellationToken.None);

        Assert.Contains("unknown_category", result);
        Assert.Equal(0, await db.AiPendingActions.CountAsync());
    }

    [Fact]
    public async Task SetBudget_UnknownCategory_ReturnsError_NoActionStaged()
    {
        await using var db = CreateDbContext();
        var userId = Guid.NewGuid();
        var service = CreateService(db);
        var tool = new SetBudgetTool(db, service);

        var result = await tool.ExecuteAsync("""{"categoryName":"NãoExiste","month":"2026-05","amount":100}""", userId, CancellationToken.None);

        Assert.Contains("unknown_category", result);
        Assert.Equal(0, await db.AiPendingActions.CountAsync());
    }

    [Fact]
    public async Task Recategorize_UnknownToCategory_ReturnsError_NoActionStaged()
    {
        await using var db = CreateDbContext();
        var userId = Guid.NewGuid();
        var service = CreateService(db);
        var tool = new RecategorizeTransactionsTool(db, service);

        var result = await tool.ExecuteAsync("""{"toCategoryName":"NãoExiste"}""", userId, CancellationToken.None);

        Assert.Contains("unknown_category", result);
        Assert.Equal(0, await db.AiPendingActions.CountAsync());
    }

    // ---- confirm executes EXACTLY ONCE + idempotency (T-17-19) --------------------------------

    [Fact]
    public async Task Confirm_PendingCreateGoal_ExecutesOnce_FlipsConfirmed()
    {
        await using var db = CreateDbContext();
        var userId = Guid.NewGuid();
        var service = CreateService(db);
        var tool = new CreateGoalTool(service);
        await tool.ExecuteAsync("""{"name":"Viagem","targetAmount":5000}""", userId, CancellationToken.None);
        var action = await db.AiPendingActions.SingleAsync();

        var outcome = await service.ConfirmAsync(userId, action.Id, CancellationToken.None);

        Assert.Equal(PendingActionOutcome.Success, outcome);
        var goals = await db.Goals.ToListAsync();
        Assert.Single(goals);
        Assert.Equal("Viagem", goals[0].Name);
        Assert.Equal(5000m, goals[0].TargetAmount);
        var reloaded = await db.AiPendingActions.SingleAsync();
        Assert.Equal(PendingActionStatus.Confirmed, reloaded.Status);
        Assert.NotNull(reloaded.DecidedAt);
    }

    [Fact]
    public async Task Confirm_AlreadyConfirmed_ReturnsConflict_NoDoubleApply()
    {
        await using var db = CreateDbContext();
        var userId = Guid.NewGuid();
        var service = CreateService(db);
        var tool = new CreateGoalTool(service);
        await tool.ExecuteAsync("""{"name":"Viagem","targetAmount":5000}""", userId, CancellationToken.None);
        var action = await db.AiPendingActions.SingleAsync();

        var first = await service.ConfirmAsync(userId, action.Id, CancellationToken.None);
        var second = await service.ConfirmAsync(userId, action.Id, CancellationToken.None);

        Assert.Equal(PendingActionOutcome.Success, first);
        Assert.Equal(PendingActionOutcome.Conflict, second);
        Assert.Equal(1, await db.Goals.CountAsync()); // still exactly one — no double-apply
    }

    [Fact]
    public async Task Confirm_ExpiredAction_ReturnsConflict_SetsExpired_NoWrite()
    {
        await using var db = CreateDbContext();
        var userId = Guid.NewGuid();
        var service = CreateService(db);
        // Stage directly with a CreatedAt older than the 24h window.
        var stale = DateTime.UtcNow - AiPendingActionService.ExpiryWindow - TimeSpan.FromMinutes(5);
        db.AiPendingActions.Add(new AiPendingAction
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            ActionType = PendingActionType.CreateGoal,
            Payload = """{"name":"Antiga","targetAmount":100}""",
            Status = PendingActionStatus.Pending,
            CreatedAt = stale,
            UpdatedAt = stale,
        });
        await db.SaveChangesAsync();
        var actionId = (await db.AiPendingActions.SingleAsync()).Id;

        var outcome = await service.ConfirmAsync(userId, actionId, CancellationToken.None);

        Assert.Equal(PendingActionOutcome.Conflict, outcome);
        Assert.Equal(0, await db.Goals.CountAsync());
        Assert.Equal(PendingActionStatus.Expired, (await db.AiPendingActions.SingleAsync()).Status);
    }

    // ---- cancel discards without a write ------------------------------------------------------

    [Fact]
    public async Task Cancel_PendingAction_SetsCancelled_NoWrite_ThenConfirmConflicts()
    {
        await using var db = CreateDbContext();
        var userId = Guid.NewGuid();
        var service = CreateService(db);
        var tool = new CreateGoalTool(service);
        await tool.ExecuteAsync("""{"name":"Viagem","targetAmount":5000}""", userId, CancellationToken.None);
        var action = await db.AiPendingActions.SingleAsync();

        var cancelled = await service.CancelAsync(userId, action.Id, CancellationToken.None);
        var confirmAfterCancel = await service.ConfirmAsync(userId, action.Id, CancellationToken.None);

        Assert.Equal(PendingActionOutcome.Success, cancelled);
        Assert.Equal(PendingActionOutcome.Conflict, confirmAfterCancel); // cancelled can never be confirmed
        Assert.Equal(0, await db.Goals.CountAsync());
        Assert.Equal(PendingActionStatus.Cancelled, (await db.AiPendingActions.SingleAsync()).Status);
    }

    // ---- UserId scoping / IDOR (T-17-20) ------------------------------------------------------

    [Fact]
    public async Task Confirm_ForeignUser_ReturnsNotFound_NoWrite_LeavesPending()
    {
        await using var db = CreateDbContext();
        var owner = Guid.NewGuid();
        var attacker = Guid.NewGuid();
        var service = CreateService(db);
        var tool = new CreateGoalTool(service);
        await tool.ExecuteAsync("""{"name":"Viagem","targetAmount":5000}""", owner, CancellationToken.None);
        var action = await db.AiPendingActions.SingleAsync();

        var outcome = await service.ConfirmAsync(attacker, action.Id, CancellationToken.None);

        Assert.Equal(PendingActionOutcome.NotFound, outcome);
        Assert.Equal(0, await db.Goals.CountAsync());
        Assert.Equal(PendingActionStatus.Pending, (await db.AiPendingActions.SingleAsync()).Status);
    }

    [Fact]
    public async Task Cancel_ForeignUser_ReturnsNotFound_LeavesPending()
    {
        await using var db = CreateDbContext();
        var owner = Guid.NewGuid();
        var attacker = Guid.NewGuid();
        var service = CreateService(db);
        var tool = new CreateGoalTool(service);
        await tool.ExecuteAsync("""{"name":"Viagem","targetAmount":5000}""", owner, CancellationToken.None);
        var action = await db.AiPendingActions.SingleAsync();

        var outcome = await service.CancelAsync(attacker, action.Id, CancellationToken.None);

        Assert.Equal(PendingActionOutcome.NotFound, outcome);
        Assert.Equal(PendingActionStatus.Pending, (await db.AiPendingActions.SingleAsync()).Status);
    }

    // ---- confirm dispatches to CreateRule + Recategorize (pure-EF domain writes) --------------

    [Fact]
    public async Task Confirm_PendingCreateRule_CreatesExactlyOneRule_ForResolvedCategory()
    {
        await using var db = CreateDbContext();
        var userId = Guid.NewGuid();
        var transporte = await SeedCategoryAsync(db, userId, "Transporte");
        var service = CreateService(db);
        var tool = new CreateCategoryRuleTool(db, service);
        await tool.ExecuteAsync("""{"pattern":"UBER","matchType":"contains","categoryName":"Transporte"}""", userId, CancellationToken.None);
        var action = await db.AiPendingActions.SingleAsync();

        var outcome = await service.ConfirmAsync(userId, action.Id, CancellationToken.None);

        Assert.Equal(PendingActionOutcome.Success, outcome);
        var rules = await db.CategoryRules.ToListAsync();
        Assert.Single(rules);
        Assert.Equal(transporte.Id, rules[0].CategoryId);
        Assert.Equal(RuleMatchOperator.Contains, rules[0].MatchOperator);
        Assert.Equal("UBER", rules[0].MatchValue);
    }

    [Fact]
    public async Task Confirm_PendingRecategorize_UpdatesMatching_ExcludesUserOverride()
    {
        await using var db = CreateDbContext();
        var userId = Guid.NewGuid();
        var transporte = await SeedCategoryAsync(db, userId, "Transporte");
        var eligible = await SeedTransactionAsync(db, userId, "UBER TRIP 1", 30m, categoryId: null, CategorySource.SystemMapping);
        var manual = await SeedTransactionAsync(db, userId, "UBER TRIP 2", 40m, categoryId: null, CategorySource.UserOverride);
        var service = CreateService(db);
        var tool = new RecategorizeTransactionsTool(db, service);
        await tool.ExecuteAsync("""{"filter":{"merchant":"uber"},"toCategoryName":"Transporte"}""", userId, CancellationToken.None);
        var action = await db.AiPendingActions.SingleAsync();

        var outcome = await service.ConfirmAsync(userId, action.Id, CancellationToken.None);

        Assert.Equal(PendingActionOutcome.Success, outcome);
        Assert.Equal(transporte.Id, (await db.Transactions.FirstAsync(t => t.Id == eligible.Id)).CategoryId);
        // A manually-overridden transaction is never clobbered by a bulk recategorize.
        Assert.Null((await db.Transactions.FirstAsync(t => t.Id == manual.Id)).CategoryId);
    }

    private static async Task<Transaction> SeedTransactionAsync(
        SimpleFinanceDbContext db, Guid userId, string description, decimal amount, Guid? categoryId, CategorySource source)
    {
        var txn = new Transaction
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            AccountId = Guid.NewGuid(),
            PluggyTransactionId = Guid.NewGuid().ToString(),
            Date = DateTime.UtcNow.AddDays(-1),
            Description = description,
            Amount = amount,
            Type = TransactionType.Debit,
            CurrencyCode = "BRL",
            CategoryId = categoryId,
            CategorySource = source,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.Transactions.Add(txn);
        await db.SaveChangesAsync();
        return txn;
    }
}
