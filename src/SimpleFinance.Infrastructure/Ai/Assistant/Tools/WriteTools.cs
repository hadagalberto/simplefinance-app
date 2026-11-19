using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using SimpleFinance.Application.Ai;
using SimpleFinance.Application.Ai.Assistant;
using SimpleFinance.Domain.Enums;
using SimpleFinance.Infrastructure.Persistence;

namespace SimpleFinance.Infrastructure.Ai.Assistant.Tools;

/// <summary>
/// AI-CHAT-04 — shared helpers for the four WRITE tools. Every write tool is <see cref="IAssistantTool"/>
/// with <c>IsWrite = true</c>; its <c>ExecuteAsync</c> deserializes + validates its args, resolves any
/// category name against the CALLER'S OWN categories (rejecting hallucinated names, T-17-21), then calls
/// <see cref="AiPendingActionService.ProposeAsync"/> to STAGE a <c>Pending</c> action — it NEVER calls a
/// domain service and NEVER mutates a domain table (T-17-18). The tool returns a small JSON descriptor
/// carrying the staged action's id + a human-readable summary; the orchestrator (17-04) recognizes a
/// write tool's result, emits an <c>action</c> confirmation-card SSE frame, and ends the turn awaiting an
/// explicit <c>POST /api/ai/actions/{id}/confirm</c>.
/// </summary>
internal static class WriteToolResults
{
    /// <summary>The staged-proposal result the orchestrator parses into an <c>action</c> card frame.</summary>
    public static string Proposed(Guid actionId, string actionType, string summary) =>
        JsonSerializer.Serialize(new
        {
            status = "pending_confirmation",
            action = new { id = actionId, actionType, summary },
            message = "Proposta criada. Aguardando confirmação explícita do usuário — nada foi alterado ainda.",
        });

    /// <summary>Resolve a category name to the caller's own category id, case-insensitively. Null when unknown.</summary>
    public static async Task<Guid?> ResolveCategoryIdAsync(
        SimpleFinanceDbContext db, Guid userId, string name, CancellationToken ct)
    {
        var normalized = name.Trim().ToLower();
        var match = await db.Categories
            .Where(c => c.UserId == userId && c.Name.ToLower() == normalized)
            .Select(c => new { c.Id })
            .FirstOrDefaultAsync(ct);
        return match?.Id;
    }

    public static string Brl(decimal amount) => "R$ " + amount.ToString("N2", CultureInfo.GetCultureInfo("pt-BR"));
}

/// <summary>
/// AI-CHAT-04 write tool <c>create_category_rule</c>: proposes a categorization rule that assigns a
/// category to transactions whose description matches a pattern. Resolves <c>categoryName</c> to the
/// user's own category (rejects unknowns) and stages a <see cref="PendingActionType.CreateRule"/>
/// proposal. <c>startsWith</c> maps to an anchored regex; the domain has no native prefix operator.
/// </summary>
public sealed class CreateCategoryRuleTool(SimpleFinanceDbContext db, AiPendingActionService actions) : IAssistantTool
{
    private static readonly LlmTool ToolSchemaValue = new(
        "create_category_rule",
        "PROPÕE (não aplica) uma regra de categorização: transações cuja descrição casa com 'pattern' recebem a categoria informada. Requer confirmação do usuário.",
        ToolSchema.Parse("""
        {
          "type": "object",
          "properties": {
            "pattern": { "type": "string", "description": "Texto/padrão a procurar na descrição da transação" },
            "matchType": { "type": "string", "enum": ["contains", "equals", "startsWith", "regex"] },
            "categoryName": { "type": "string", "description": "Nome exato de uma categoria do usuário" }
          },
          "required": ["pattern", "matchType", "categoryName"],
          "additionalProperties": false
        }
        """));

    public string Name => "create_category_rule";
    public bool IsWrite => true;
    public LlmTool Schema => ToolSchemaValue;

    public async Task<string> ExecuteAsync(string argsJson, Guid userId, CancellationToken ct)
    {
        var args = JsonSerializer.Deserialize<Args>(argsJson) ?? new Args(null, null, null);

        if (string.IsNullOrWhiteSpace(args.Pattern) || string.IsNullOrWhiteSpace(args.CategoryName))
        {
            return ToolResults.Error("invalid_arguments", "pattern e categoryName são obrigatórios.");
        }

        if (!TryMapOperator(args.MatchType, args.Pattern!, out var matchOperator, out var matchValue))
        {
            return ToolResults.Error("invalid_arguments", "matchType deve ser contains, equals, startsWith ou regex.");
        }

        var categoryId = await WriteToolResults.ResolveCategoryIdAsync(db, userId, args.CategoryName!, ct);
        if (categoryId is null)
        {
            return ToolResults.Error("unknown_category", $"Nenhuma categoria chamada '{args.CategoryName}' pertence ao usuário.");
        }

        var payload = JsonSerializer.Serialize(new
        {
            matchField = nameof(RuleMatchField.Description),
            matchOperator = matchOperator.ToString(),
            matchValue,
            categoryId = categoryId.Value,
            categoryName = args.CategoryName,
        });

        var action = await actions.ProposeAsync(userId, null, PendingActionType.CreateRule, payload, ct);
        return WriteToolResults.Proposed(action.Id, Name,
            $"Criar regra: transações com descrição contendo \"{args.Pattern}\" → categoria \"{args.CategoryName}\".");
    }

    // The domain supports Contains/Equals/Regex/Range; startsWith is expressed as an anchored,
    // escaped regex since there is no native prefix operator.
    private static bool TryMapOperator(string? matchType, string pattern, out RuleMatchOperator op, out string matchValue)
    {
        op = RuleMatchOperator.Contains;
        matchValue = pattern;

        switch (matchType?.Trim().ToLowerInvariant())
        {
            case "contains":
                op = RuleMatchOperator.Contains;
                return true;
            case "equals":
                op = RuleMatchOperator.Equals;
                return true;
            case "regex":
                op = RuleMatchOperator.Regex;
                return true;
            case "startswith":
                op = RuleMatchOperator.Regex;
                matchValue = "^" + System.Text.RegularExpressions.Regex.Escape(pattern);
                return true;
            default:
                return false;
        }
    }

    private sealed record Args(
        [property: JsonPropertyName("pattern")] string? Pattern,
        [property: JsonPropertyName("matchType")] string? MatchType,
        [property: JsonPropertyName("categoryName")] string? CategoryName);
}

/// <summary>
/// AI-CHAT-04 write tool <c>set_budget</c>: proposes a monthly spending limit for a category. Resolves
/// <c>categoryName</c> (rejects unknowns), validates the <c>YYYY-MM</c> month and a positive amount, and
/// stages a <see cref="PendingActionType.SetBudget"/> proposal.
/// </summary>
public sealed class SetBudgetTool(SimpleFinanceDbContext db, AiPendingActionService actions) : IAssistantTool
{
    private static readonly LlmTool ToolSchemaValue = new(
        "set_budget",
        "PROPÕE (não aplica) um limite de orçamento mensal para uma categoria. Requer confirmação do usuário.",
        ToolSchema.Parse("""
        {
          "type": "object",
          "properties": {
            "categoryName": { "type": "string", "description": "Nome exato de uma categoria do usuário" },
            "month": { "type": "string", "description": "Mês no formato YYYY-MM" },
            "amount": { "type": "number", "description": "Limite mensal em BRL, maior que zero" }
          },
          "required": ["categoryName", "month", "amount"],
          "additionalProperties": false
        }
        """));

    public string Name => "set_budget";
    public bool IsWrite => true;
    public LlmTool Schema => ToolSchemaValue;

    public async Task<string> ExecuteAsync(string argsJson, Guid userId, CancellationToken ct)
    {
        var args = JsonSerializer.Deserialize<Args>(argsJson) ?? new Args(null, null, null);

        if (string.IsNullOrWhiteSpace(args.CategoryName) || args.Amount is null || args.Amount.Value <= 0m)
        {
            return ToolResults.Error("invalid_arguments", "categoryName e um amount positivo são obrigatórios.");
        }

        if (!DateTime.TryParseExact(args.Month, "yyyy-MM", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsedMonth))
        {
            return ToolResults.Error("invalid_period", "month deve estar no formato YYYY-MM.");
        }

        var categoryId = await WriteToolResults.ResolveCategoryIdAsync(db, userId, args.CategoryName!, ct);
        if (categoryId is null)
        {
            return ToolResults.Error("unknown_category", $"Nenhuma categoria chamada '{args.CategoryName}' pertence ao usuário.");
        }

        var payload = JsonSerializer.Serialize(new
        {
            categoryId = categoryId.Value,
            categoryName = args.CategoryName,
            year = parsedMonth.Year,
            month = parsedMonth.Month,
            amount = args.Amount.Value,
        });

        var action = await actions.ProposeAsync(userId, null, PendingActionType.SetBudget, payload, ct);
        return WriteToolResults.Proposed(action.Id, Name,
            $"Definir orçamento de {WriteToolResults.Brl(args.Amount.Value)} para \"{args.CategoryName}\" em {args.Month}.");
    }

    private sealed record Args(
        [property: JsonPropertyName("categoryName")] string? CategoryName,
        [property: JsonPropertyName("month")] string? Month,
        [property: JsonPropertyName("amount")] decimal? Amount);
}

/// <summary>
/// AI-CHAT-04 write tool <c>create_goal</c>: proposes a new savings goal. Validates a non-empty name and
/// a positive target amount, parses the optional <c>YYYY-MM-DD</c> target date, and stages a
/// <see cref="PendingActionType.CreateGoal"/> proposal. Goals are always manually tracked (no linked
/// account/investment).
/// </summary>
public sealed class CreateGoalTool(AiPendingActionService actions) : IAssistantTool
{
    private static readonly LlmTool ToolSchemaValue = new(
        "create_goal",
        "PROPÕE (não cria) uma meta de economia. Requer confirmação do usuário.",
        ToolSchema.Parse("""
        {
          "type": "object",
          "properties": {
            "name": { "type": "string" },
            "targetAmount": { "type": "number", "description": "Valor alvo em BRL, maior que zero" },
            "targetDate": { "type": "string", "description": "Data alvo opcional no formato YYYY-MM-DD" }
          },
          "required": ["name", "targetAmount"],
          "additionalProperties": false
        }
        """));

    public string Name => "create_goal";
    public bool IsWrite => true;
    public LlmTool Schema => ToolSchemaValue;

    public async Task<string> ExecuteAsync(string argsJson, Guid userId, CancellationToken ct)
    {
        var args = JsonSerializer.Deserialize<Args>(argsJson) ?? new Args(null, null, null);

        if (string.IsNullOrWhiteSpace(args.Name) || args.TargetAmount is null || args.TargetAmount.Value <= 0m)
        {
            return ToolResults.Error("invalid_arguments", "name e um targetAmount positivo são obrigatórios.");
        }

        string? targetDate = null;
        if (!string.IsNullOrWhiteSpace(args.TargetDate))
        {
            if (!DateTime.TryParseExact(args.TargetDate, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out _))
            {
                return ToolResults.Error("invalid_arguments", "targetDate deve estar no formato YYYY-MM-DD.");
            }

            targetDate = args.TargetDate;
        }

        var payload = JsonSerializer.Serialize(new
        {
            name = args.Name,
            targetAmount = args.TargetAmount.Value,
            targetDate,
        });

        var action = await actions.ProposeAsync(userId, null, PendingActionType.CreateGoal, payload, ct);
        var when = targetDate is null ? string.Empty : $" até {targetDate}";
        return WriteToolResults.Proposed(action.Id, Name,
            $"Criar meta \"{args.Name}\" de {WriteToolResults.Brl(args.TargetAmount.Value)}{when}.");
    }

    private sealed record Args(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("targetAmount")] decimal? TargetAmount,
        [property: JsonPropertyName("targetDate")] string? TargetDate);
}

/// <summary>
/// AI-CHAT-04 write tool <c>recategorize_transactions</c>: proposes a bulk recategorization of the
/// caller's transactions matching a filter (optional period / merchant / current category) to a target
/// category. Resolves the target (and optional current) category name (rejects unknowns), resolves the
/// period to an ABSOLUTE UTC range at propose time (so a relative period can't drift by confirm time),
/// caps the count, and stages a <see cref="PendingActionType.Recategorize"/> proposal. Execution excludes
/// manually-overridden transactions and sets the new source to <c>UserOverride</c>.
/// </summary>
public sealed class RecategorizeTransactionsTool(SimpleFinanceDbContext db, AiPendingActionService actions) : IAssistantTool
{
    private const int DefaultMaxCount = 500;

    private static readonly LlmTool ToolSchemaValue = new(
        "recategorize_transactions",
        "PROPÕE (não aplica) recategorizar em massa as transações que casam com o filtro para outra categoria. Requer confirmação do usuário.",
        ToolSchema.Parse("""
        {
          "type": "object",
          "properties": {
            "filter": {
              "type": "object",
              "properties": {
                "period": { "type": "string", "description": "YYYY-MM | YYYY | last_7_days | last_30_days | last_90_days | this_month | last_month | ytd" },
                "merchant": { "type": "string" },
                "currentCategoryName": { "type": "string", "description": "Nome exato de uma categoria do usuário" }
              },
              "additionalProperties": false
            },
            "toCategoryName": { "type": "string", "description": "Nome exato de uma categoria do usuário" },
            "maxCount": { "type": "integer", "minimum": 1, "maximum": 500 }
          },
          "required": ["toCategoryName"],
          "additionalProperties": false
        }
        """));

    public string Name => "recategorize_transactions";
    public bool IsWrite => true;
    public LlmTool Schema => ToolSchemaValue;

    public async Task<string> ExecuteAsync(string argsJson, Guid userId, CancellationToken ct)
    {
        var args = JsonSerializer.Deserialize<Args>(argsJson) ?? new Args(null, null, null);

        if (string.IsNullOrWhiteSpace(args.ToCategoryName))
        {
            return ToolResults.Error("invalid_arguments", "toCategoryName é obrigatório.");
        }

        var toCategoryId = await WriteToolResults.ResolveCategoryIdAsync(db, userId, args.ToCategoryName!, ct);
        if (toCategoryId is null)
        {
            return ToolResults.Error("unknown_category", $"Nenhuma categoria chamada '{args.ToCategoryName}' pertence ao usuário.");
        }

        Guid? currentCategoryId = null;
        string? startUtc = null;
        string? endUtc = null;
        var filter = args.Filter;

        if (filter is not null)
        {
            if (!string.IsNullOrWhiteSpace(filter.CurrentCategoryName))
            {
                currentCategoryId = await WriteToolResults.ResolveCategoryIdAsync(db, userId, filter.CurrentCategoryName!, ct);
                if (currentCategoryId is null)
                {
                    return ToolResults.Error("unknown_category", $"Nenhuma categoria chamada '{filter.CurrentCategoryName}' pertence ao usuário.");
                }
            }

            if (!string.IsNullOrWhiteSpace(filter.Period))
            {
                if (!PeriodParser.TryResolve(filter.Period, out var range))
                {
                    return ToolResults.Error("invalid_period");
                }

                startUtc = range.Start.ToString("O", CultureInfo.InvariantCulture);
                endUtc = range.End.ToString("O", CultureInfo.InvariantCulture);
            }
        }

        var maxCount = args.MaxCount is int m ? Math.Clamp(m, 1, DefaultMaxCount) : DefaultMaxCount;

        var payload = JsonSerializer.Serialize(new
        {
            startUtc,
            endUtc,
            merchant = filter?.Merchant,
            currentCategoryId,
            toCategoryId = toCategoryId.Value,
            toCategoryName = args.ToCategoryName,
            maxCount,
        });

        var action = await actions.ProposeAsync(userId, null, PendingActionType.Recategorize, payload, ct);

        var scope = filter?.CurrentCategoryName is { Length: > 0 } cur
            ? $"de \"{cur}\" "
            : string.Empty;
        var period = filter?.Period is { Length: > 0 } p ? $" ({p})" : string.Empty;
        return WriteToolResults.Proposed(action.Id, Name,
            $"Recategorizar até {maxCount} transações {scope}para \"{args.ToCategoryName}\"{period}.");
    }

    private sealed record Args(
        [property: JsonPropertyName("filter")] Filter? Filter,
        [property: JsonPropertyName("toCategoryName")] string? ToCategoryName,
        [property: JsonPropertyName("maxCount")] int? MaxCount);

    private sealed record Filter(
        [property: JsonPropertyName("period")] string? Period,
        [property: JsonPropertyName("merchant")] string? Merchant,
        [property: JsonPropertyName("currentCategoryName")] string? CurrentCategoryName);
}
