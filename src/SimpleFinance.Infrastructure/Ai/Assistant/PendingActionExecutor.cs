using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SimpleFinance.Domain.Entities;
using SimpleFinance.Domain.Enums;
using SimpleFinance.Infrastructure.Budgets;
using SimpleFinance.Infrastructure.Goals;
using SimpleFinance.Infrastructure.Persistence;

namespace SimpleFinance.Infrastructure.Ai.Assistant;

/// <summary>
/// AI-CHAT-04 — the confirm-time dispatch that turns a <see cref="AiPendingAction"/> into the ONE real
/// write. Called only from <see cref="AiPendingActionService.ConfirmAsync"/> (never from a write-tool),
/// it defensively <c>TryParse</c>s the jsonb <see cref="AiPendingAction.Payload"/> and switches on
/// <see cref="AiPendingAction.ActionType"/> to the matching EXISTING domain service —
/// <see cref="GoalService.CreateGoalAsync"/>, <see cref="BudgetService.UpsertBudgetAsync"/>, the
/// CategoryRule build replicated from <c>CategoryRulesEndpoints.CreateCategoryRuleAsync</c>, and the
/// bulk recategorize loop modeled on <c>CategoryRulesEndpoints.ApplyRuleNowAsync</c>. Every branch is
/// UserId-scoped (defense-in-depth). Goal/Budget go through their own self-saving services; the
/// CreateRule/Recategorize branches only STAGE entities into the shared context so the caller's single
/// trailing <c>SaveChangesAsync</c> commits them together with the status transition.
///
/// Returns <c>true</c> when the payload was usable and the dispatch ran (a recategorize that matched
/// zero rows still counts as applied); <c>false</c> only when the payload is corrupt/unresolvable, so
/// the caller can reject the confirm without mutating anything.
/// </summary>
public sealed class PendingActionExecutor(SimpleFinanceDbContext db, GoalService goals, BudgetService budgets)
{
    private const int RecategorizeMaxCap = 500;

    public async Task<bool> ExecuteAsync(AiPendingAction action, Guid userId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(action.Payload))
        {
            return false;
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(action.Payload);
        }
        catch (JsonException)
        {
            return false;
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            return action.ActionType switch
            {
                PendingActionType.CreateGoal => await ExecuteCreateGoalAsync(root, userId, ct),
                PendingActionType.SetBudget => await ExecuteSetBudgetAsync(root, userId, ct),
                PendingActionType.CreateRule => ExecuteCreateRule(root, userId),
                PendingActionType.Recategorize => await ExecuteRecategorizeAsync(root, userId, ct),
                _ => false,
            };
        }
    }

    // CreateGoal → GoalService.CreateGoalAsync (self-saving domain service). Never linked to an
    // account/investment (a proposed goal is always manually tracked), so both link ids are null.
    private async Task<bool> ExecuteCreateGoalAsync(JsonElement root, Guid userId, CancellationToken ct)
    {
        var name = GetString(root, "name");
        if (string.IsNullOrWhiteSpace(name) || !TryGetDecimal(root, "targetAmount", out var targetAmount) || targetAmount <= 0m)
        {
            return false;
        }

        DateTime? targetDate = null;
        var targetDateRaw = GetString(root, "targetDate");
        if (!string.IsNullOrWhiteSpace(targetDateRaw))
        {
            if (DateTime.TryParseExact(targetDateRaw, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
            {
                targetDate = DateTime.SpecifyKind(parsed.Date, DateTimeKind.Utc);
            }
        }

        await goals.CreateGoalAsync(userId, name!, targetAmount, targetDate, null, null, null, null, ct);
        return true;
    }

    // SetBudget → BudgetService.UpsertBudgetAsync (self-saving, re-verifies category ownership).
    private async Task<bool> ExecuteSetBudgetAsync(JsonElement root, Guid userId, CancellationToken ct)
    {
        if (!TryGetGuid(root, "categoryId", out var categoryId)
            || !TryGetInt(root, "year", out var year)
            || !TryGetInt(root, "month", out var month)
            || !TryGetDecimal(root, "amount", out var amount)
            || amount <= 0m
            || month is < 1 or > 12)
        {
            return false;
        }

        var row = await budgets.UpsertBudgetAsync(userId, categoryId, year, month, amount, ct);
        // Null means the category no longer belongs to the user (deleted between propose and confirm).
        return row is not null;
    }

    // CreateRule → replicates CategoryRulesEndpoints.CreateCategoryRuleAsync's entity build (priority
    // defaults to max+1). STAGED only — committed by the caller's single trailing SaveChangesAsync.
    private bool ExecuteCreateRule(JsonElement root, Guid userId)
    {
        if (!TryGetGuid(root, "categoryId", out var categoryId)
            || !TryGetEnum<RuleMatchField>(root, "matchField", out var matchField)
            || !TryGetEnum<RuleMatchOperator>(root, "matchOperator", out var matchOperator))
        {
            return false;
        }

        var matchValue = GetString(root, "matchValue");

        // Priority = (max existing for this user) + 1 so a new rule never jumps evaluation order.
        var maxPriority = db.CategoryRules
            .Where(r => r.UserId == userId)
            .Select(r => (int?)r.Priority)
            .Max();
        var priority = (maxPriority ?? -1) + 1;

        var now = DateTime.UtcNow;
        db.CategoryRules.Add(new CategoryRule
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            MatchField = matchField,
            MatchOperator = matchOperator,
            MatchValue = matchValue,
            AmountMin = null,
            AmountMax = null,
            CategoryId = categoryId,
            Priority = priority,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        });

        return true;
    }

    // Recategorize → bulk set-CategoryId loop modeled on CategoryRulesEndpoints.ApplyRuleNowAsync.
    // ALWAYS excludes CategorySource.UserOverride (never clobbers a manual choice), applies the
    // proposal's filter (resolved absolute period / merchant / current category), caps at maxCount, and
    // sets the new source to UserOverride (a user-confirmed decision). STAGED only.
    private async Task<bool> ExecuteRecategorizeAsync(JsonElement root, Guid userId, CancellationToken ct)
    {
        if (!TryGetGuid(root, "toCategoryId", out var toCategoryId))
        {
            return false;
        }

        var maxCount = TryGetInt(root, "maxCount", out var mc) ? Math.Clamp(mc, 1, RecategorizeMaxCap) : RecategorizeMaxCap;

        var query = db.Transactions.Where(t =>
            t.UserId == userId &&
            t.CategorySource != CategorySource.UserOverride &&
            !t.IsTransfer && !t.IsHidden);

        if (DateTime.TryParse(GetString(root, "startUtc"), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var start)
            && DateTime.TryParse(GetString(root, "endUtc"), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var end))
        {
            query = query.Where(t => t.Date >= start && t.Date <= end);
        }

        var merchant = GetString(root, "merchant");
        if (!string.IsNullOrWhiteSpace(merchant))
        {
            var m = merchant.Trim().ToLower();
            query = query.Where(t =>
                (t.MerchantName != null && t.MerchantName.ToLower().Contains(m)) ||
                t.Description.ToLower().Contains(m));
        }

        if (TryGetGuid(root, "currentCategoryId", out var currentCategoryId))
        {
            query = query.Where(t => t.CategoryId == currentCategoryId);
        }

        var candidates = await query
            .OrderByDescending(t => t.Date)
            .Take(maxCount)
            .ToListAsync(ct);

        var now = DateTime.UtcNow;
        foreach (var transaction in candidates)
        {
            transaction.CategoryId = toCategoryId;
            transaction.CategorySource = CategorySource.UserOverride;
            transaction.UpdatedAt = now;
        }

        return true;
    }

    private static string? GetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static bool TryGetGuid(JsonElement root, string name, out Guid value)
    {
        value = default;
        return root.TryGetProperty(name, out var p)
            && p.ValueKind == JsonValueKind.String
            && Guid.TryParse(p.GetString(), out value);
    }

    private static bool TryGetInt(JsonElement root, string name, out int value)
    {
        value = default;
        return root.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out value);
    }

    private static bool TryGetDecimal(JsonElement root, string name, out decimal value)
    {
        value = default;
        return root.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetDecimal(out value);
    }

    private static bool TryGetEnum<TEnum>(JsonElement root, string name, out TEnum value) where TEnum : struct, Enum
    {
        value = default;
        var raw = GetString(root, name);
        return raw is not null && Enum.TryParse(raw, ignoreCase: true, out value);
    }
}
