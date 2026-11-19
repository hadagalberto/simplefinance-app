using System.Text.Json;
using System.Text.Json.Serialization;
using SimpleFinance.Application.Ai;
using SimpleFinance.Application.Ai.Assistant;
using SimpleFinance.Infrastructure.Reporting;

namespace SimpleFinance.Infrastructure.Ai.Assistant.Tools;

/// <summary>
/// AI-CHAT-02 read tool <c>sum_by_category</c>: spend broken down by category for a period. Wraps
/// <see cref="ICategorySpendReader.GetCategorySpendAsync"/> (the same expense aggregate the dashboard
/// uses — already excludes transfers/hidden/soft-deleted and is scoped to the caller). Returns a
/// compact, rounded top-20 list; the category breakdown is expense-only (the underlying query sums
/// debits), so an <c>income</c> request is answered honestly with an <c>unsupported_type</c> error
/// rather than a wrong number.
/// </summary>
public sealed class SumByCategoryTool(ICategorySpendReader reader) : IAssistantTool
{
    private const int MaxItems = 20;

    private static readonly LlmTool ToolSchemaValue = new(
        "sum_by_category",
        "Total gasto por categoria em um período. type deve ser \"expense\" (a quebra por categoria é de despesas).",
        ToolSchema.Parse("""
        {
          "type": "object",
          "properties": {
            "period": { "type": "string", "description": "YYYY-MM | YYYY | last_7_days | last_30_days | last_90_days | this_month | last_month | ytd" },
            "type": { "type": "string", "enum": ["income", "expense"] }
          },
          "required": ["period", "type"],
          "additionalProperties": false
        }
        """));

    public string Name => "sum_by_category";
    public bool IsWrite => false;
    public LlmTool Schema => ToolSchemaValue;

    public async Task<string> ExecuteAsync(string argsJson, Guid userId, CancellationToken ct)
    {
        var args = JsonSerializer.Deserialize<Args>(argsJson) ?? new Args(null, null);

        if (!string.Equals(args.Type, "expense", StringComparison.OrdinalIgnoreCase))
        {
            return ToolResults.Error("unsupported_type", "A quebra por categoria está disponível apenas para despesas (type=expense).");
        }

        if (!PeriodParser.TryResolve(args.Period, out var range))
        {
            return ToolResults.Error("invalid_period");
        }

        var rows = await reader.GetCategorySpendAsync(userId, range.Start, range.End, ct);

        var items = rows
            .Take(MaxItems)
            .Select(r => new { category = r.CategoryName ?? "Sem categoria", amount = Math.Round(r.Amount, 2) })
            .ToList();

        return JsonSerializer.Serialize(new
        {
            period = args.Period,
            type = "expense",
            currency = "BRL",
            items,
        });
    }

    private sealed record Args(
        [property: JsonPropertyName("period")] string? Period,
        [property: JsonPropertyName("type")] string? Type);
}
