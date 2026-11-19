using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using SimpleFinance.Application.Ai;
using SimpleFinance.Application.Ai.Assistant;
using SimpleFinance.Domain.Enums;
using SimpleFinance.Infrastructure.Persistence;

namespace SimpleFinance.Infrastructure.Ai.Assistant.Tools;

/// <summary>
/// AI-CHAT-02 read tool <c>query_transactions</c>: a filtered transaction query that returns an
/// AGGREGATE (count + total + top-N), never a full row dump (T-17-06). Models the same filter
/// predicates as <c>TransactionsEndpoints.GetTransactionsAsync</c> but scoped to the caller and reduced
/// to a compact result. A category name that does not resolve to one of the user's own categories is
/// rejected as <c>unknown_category</c> (T-17-05, mirrors Phase 16's hallucinated-id rejection). Every
/// free-text field in the top-N is PII-redacted before it can enter the model context.
/// </summary>
public sealed class QueryTransactionsTool(SimpleFinanceDbContext db, IPiiRedactor redactor) : IAssistantTool
{
    private const int TopN = 5;

    private static readonly LlmTool ToolSchemaValue = new(
        "query_transactions",
        "Consulta agregada de transações (contagem + total + maiores lançamentos) para um período, com filtros opcionais. Nunca retorna o histórico completo.",
        ToolSchema.Parse("""
        {
          "type": "object",
          "properties": {
            "period": { "type": "string", "description": "YYYY-MM | YYYY | last_7_days | last_30_days | last_90_days | this_month | last_month | ytd" },
            "category": { "type": "string", "description": "Nome exato de uma categoria do usuário" },
            "uncategorized": { "type": "boolean", "description": "true = apenas transações SEM categoria (categoria nula/'Sem categoria'). Use isto em vez de category para investigar gastos não categorizados." },
            "merchant": { "type": "string" },
            "min": { "type": "number" },
            "max": { "type": "number" },
            "type": { "type": "string", "enum": ["income", "expense"] }
          },
          "required": ["period"],
          "additionalProperties": false
        }
        """));

    public string Name => "query_transactions";
    public bool IsWrite => false;
    public LlmTool Schema => ToolSchemaValue;

    public async Task<string> ExecuteAsync(string argsJson, Guid userId, CancellationToken ct)
    {
        var args = JsonSerializer.Deserialize<Args>(argsJson) ?? new Args(null, null, null, null, null, null, null);

        if (!PeriodParser.TryResolve(args.Period, out var range))
        {
            return ToolResults.Error("invalid_period");
        }

        // A transfer is never real income/expense and a hidden row is deliberately excluded from
        // reporting — keep the aggregate consistent with the dashboard's money-correctness rules.
        var query = db.Transactions.Where(t =>
            t.UserId == userId &&
            t.Date >= range.Start && t.Date <= range.End &&
            !t.IsTransfer && !t.IsHidden);

        // Uncategorized filter takes precedence over a category name: "gastos sem categoria" means
        // CategoryId is null (there is no real "Sem categoria" category to resolve).
        if (args.Uncategorized == true)
        {
            query = query.Where(t => t.CategoryId == null);
        }
        else if (!string.IsNullOrWhiteSpace(args.Category))
        {
            var name = args.Category.Trim().ToLower();
            var category = await db.Categories
                .Where(c => c.UserId == userId && c.Name.ToLower() == name)
                .Select(c => new { c.Id })
                .FirstOrDefaultAsync(ct);

            if (category is null)
            {
                return ToolResults.Error("unknown_category", $"Nenhuma categoria chamada '{args.Category}' pertence ao usuário.");
            }

            query = query.Where(t => t.CategoryId == category.Id);
        }

        if (!string.IsNullOrWhiteSpace(args.Merchant))
        {
            var merchant = args.Merchant.Trim().ToLower();
            query = query.Where(t =>
                (t.MerchantName != null && t.MerchantName.ToLower().Contains(merchant)) ||
                t.Description.ToLower().Contains(merchant));
        }

        if (args.Min is not null)
        {
            query = query.Where(t => Math.Abs(t.Amount) >= args.Min.Value);
        }

        if (args.Max is not null)
        {
            query = query.Where(t => Math.Abs(t.Amount) <= args.Max.Value);
        }

        if (!string.IsNullOrWhiteSpace(args.Type))
        {
            query = args.Type.Trim().ToLowerInvariant() switch
            {
                "income" => query.Where(t => t.Type == TransactionType.Credit),
                "expense" => query.Where(t => t.Type == TransactionType.Debit),
                _ => query,
            };
        }

        var count = await query.CountAsync(ct);
        var total = count == 0 ? 0m : await query.SumAsync(t => Math.Abs(t.Amount), ct);

        var topRows = await query
            .OrderByDescending(t => Math.Abs(t.Amount))
            .Take(TopN)
            .Select(t => new { t.Date, t.Description, t.MerchantName, t.Amount, t.CategoryId })
            .ToListAsync(ct);

        var categoryNames = await ResolveCategoryNamesAsync(topRows.Select(r => r.CategoryId), userId, ct);

        var top = topRows.Select(r => new
        {
            date = r.Date.ToString("yyyy-MM-dd"),
            description = redactor.Redact(r.MerchantName ?? r.Description),
            amount = Math.Round(Math.Abs(r.Amount), 2),
            category = r.CategoryId is not null && categoryNames.TryGetValue(r.CategoryId.Value, out var n) ? n : null,
        });

        return JsonSerializer.Serialize(new
        {
            period = args.Period,
            currency = "BRL",
            count,
            total = Math.Round(total, 2),
            top,
        });
    }

    private async Task<Dictionary<Guid, string>> ResolveCategoryNamesAsync(
        IEnumerable<Guid?> categoryIds, Guid userId, CancellationToken ct)
    {
        var ids = categoryIds.Where(id => id is not null).Select(id => id!.Value).Distinct().ToList();
        if (ids.Count == 0)
        {
            return [];
        }

        return await db.Categories
            .Where(c => c.UserId == userId && ids.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => c.Name, ct);
    }

    private sealed record Args(
        [property: JsonPropertyName("period")] string? Period,
        [property: JsonPropertyName("category")] string? Category,
        [property: JsonPropertyName("merchant")] string? Merchant,
        [property: JsonPropertyName("min")] decimal? Min,
        [property: JsonPropertyName("max")] decimal? Max,
        [property: JsonPropertyName("type")] string? Type,
        [property: JsonPropertyName("uncategorized")] bool? Uncategorized);
}
