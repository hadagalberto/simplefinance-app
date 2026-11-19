using Microsoft.EntityFrameworkCore;
using SimpleFinance.Domain.Entities;
using SimpleFinance.Domain.Enums;
using SimpleFinance.Infrastructure.Persistence;

namespace SimpleFinance.Infrastructure.Alerts;

/// <summary>
/// The full picture for a single AlertRule row, per 08-05-PLAN.md's interfaces block.
/// Type/Channel are serialized as their C# enum ToString() values (PascalCase, e.g.
/// "BudgetThreshold", "Telegram") -- matches this codebase's verified-correct
/// Transaction.Type/Status/CategorySource wire-contract convention. CategoryName/AccountName are
/// left-joined in for display, null when CategoryId/AccountId is null.
/// </summary>
public sealed record AlertRuleResult(
    Guid Id,
    string Type,
    decimal? Threshold,
    Guid? CategoryId,
    string? CategoryName,
    Guid? AccountId,
    string? AccountName,
    int? DaysBefore,
    string Channel,
    bool IsActive);

/// <summary>
/// The real, directly-testable AlertRule business logic (08-05 Task 1) behind GET/POST/PUT/
/// PATCH-active/DELETE /api/alert-rules -- AlertRulesEndpoints.cs is a thin HTTP wrapper over this
/// class only, no business logic duplicated there. Deliberately placed in Infrastructure (not
/// Application), mirroring BudgetService's/GoalService's documented rationale: it depends
/// directly on SimpleFinanceDbContext, and Application cannot reference Infrastructure without a
/// circular project reference.
/// </summary>
public sealed class AlertRuleService(SimpleFinanceDbContext db)
{
    /// <summary>
    /// NOTIF-02/CFG-01's list path -- only the caller's own rules, with Category/Account names
    /// batch-loaded (mirrors GoalService.GetGoalsAsync's Account/Investment dictionary-batching
    /// precedent, avoiding N+1 without a chained-GroupJoin LINQ query).
    /// </summary>
    public async Task<IReadOnlyList<AlertRuleResult>> GetAlertRulesAsync(Guid userId, CancellationToken ct)
    {
        var rules = await db.AlertRules
            .Where(r => r.UserId == userId)
            .OrderBy(r => r.CreatedAt)
            .ToListAsync(ct);

        if (rules.Count == 0)
        {
            return [];
        }

        var (categoriesById, accountsById) = await LoadNamesAsync(rules, ct);

        return rules.Select(r => ToResult(r, categoriesById, accountsById)).ToList();
    }

    /// <summary>
    /// NOTIF-02/CFG-01's create path. Trusts its caller's already-validated input
    /// (CreateAlertRuleValidator is the sole enforcer of type/channel parseability, CategoryId/
    /// AccountId ownership, and per-type Threshold/DaysBefore requirements).
    /// </summary>
    public async Task<AlertRuleResult> CreateAlertRuleAsync(
        Guid userId,
        AlertRuleType type,
        decimal? threshold,
        Guid? categoryId,
        Guid? accountId,
        int? daysBefore,
        NotificationChannelType channel,
        bool isActive,
        CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var rule = new AlertRule
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Type = type,
            Threshold = threshold,
            CategoryId = categoryId,
            AccountId = accountId,
            DaysBefore = daysBefore,
            Channel = channel,
            IsActive = isActive,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.AlertRules.Add(rule);
        await db.SaveChangesAsync(ct);

        return await ComputeSingleRowAsync(rule, ct);
    }

    /// <summary>
    /// NOTIF-02/CFG-01's edit path. Ownership-checked (T-08-10) -- returns null (never throws) for
    /// a missing/other-user id, mirroring GoalService.UpdateGoalAsync's contract.
    /// </summary>
    public async Task<AlertRuleResult?> UpdateAlertRuleAsync(
        Guid userId,
        Guid id,
        AlertRuleType type,
        decimal? threshold,
        Guid? categoryId,
        Guid? accountId,
        int? daysBefore,
        NotificationChannelType channel,
        bool isActive,
        CancellationToken ct)
    {
        var rule = await db.AlertRules.FirstOrDefaultAsync(r => r.Id == id && r.UserId == userId, ct);
        if (rule is null)
        {
            return null;
        }

        rule.Type = type;
        rule.Threshold = threshold;
        rule.CategoryId = categoryId;
        rule.AccountId = accountId;
        rule.DaysBefore = daysBefore;
        rule.Channel = channel;
        rule.IsActive = isActive;
        rule.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        return await ComputeSingleRowAsync(rule, ct);
    }

    /// <summary>T-08-10: scoped by (Id, UserId) -- returns null rather than throwing when
    /// missing/other-user.</summary>
    public async Task<AlertRuleResult?> ToggleActiveAsync(Guid userId, Guid id, bool isActive, CancellationToken ct)
    {
        var rule = await db.AlertRules.FirstOrDefaultAsync(r => r.Id == id && r.UserId == userId, ct);
        if (rule is null)
        {
            return null;
        }

        rule.IsActive = isActive;
        rule.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        return await ComputeSingleRowAsync(rule, ct);
    }

    /// <summary>T-08-10: scoped by (Id, UserId). Mirrors BudgetService.DeleteBudgetAsync's
    /// false-not-throw contract.</summary>
    public async Task<bool> DeleteAlertRuleAsync(Guid userId, Guid id, CancellationToken ct)
    {
        var rule = await db.AlertRules.FirstOrDefaultAsync(r => r.Id == id && r.UserId == userId, ct);
        if (rule is null)
        {
            return false;
        }

        db.AlertRules.Remove(rule);
        await db.SaveChangesAsync(ct);
        return true;
    }

    // Single-row variant used by Create/Update/ToggleActive -- fetches just the one linked
    // Category/Account (if any) for the row just written, so create/update/toggle and the list
    // path can never diverge on the derived CategoryName/AccountName fields.
    private async Task<AlertRuleResult> ComputeSingleRowAsync(AlertRule rule, CancellationToken ct)
    {
        var (categoriesById, accountsById) = await LoadNamesAsync([rule], ct);
        return ToResult(rule, categoriesById, accountsById);
    }

    private async Task<(Dictionary<Guid, string> CategoriesById, Dictionary<Guid, string> AccountsById)> LoadNamesAsync(
        IReadOnlyList<AlertRule> rules, CancellationToken ct)
    {
        var categoryIds = rules.Where(r => r.CategoryId is not null).Select(r => r.CategoryId!.Value).Distinct().ToList();
        var accountIds = rules.Where(r => r.AccountId is not null).Select(r => r.AccountId!.Value).Distinct().ToList();

        var categoriesById = categoryIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await db.Categories.Where(c => categoryIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Name, ct);

        var accountsById = accountIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await db.Accounts.Where(a => accountIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, a => a.Name, ct);

        return (categoriesById, accountsById);
    }

    private static AlertRuleResult ToResult(
        AlertRule rule, Dictionary<Guid, string> categoriesById, Dictionary<Guid, string> accountsById) => new(
        rule.Id,
        rule.Type.ToString(),
        rule.Threshold,
        rule.CategoryId,
        rule.CategoryId is not null && categoriesById.TryGetValue(rule.CategoryId.Value, out var categoryName) ? categoryName : null,
        rule.AccountId,
        rule.AccountId is not null && accountsById.TryGetValue(rule.AccountId.Value, out var accountName) ? accountName : null,
        rule.DaysBefore,
        rule.Channel.ToString(),
        rule.IsActive);
}
