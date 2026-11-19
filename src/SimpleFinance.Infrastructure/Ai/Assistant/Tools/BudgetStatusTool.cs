using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using SimpleFinance.Application.Ai;
using SimpleFinance.Application.Ai.Assistant;
using SimpleFinance.Infrastructure.Budgets;

namespace SimpleFinance.Infrastructure.Ai.Assistant.Tools;

/// <summary>
/// AI-CHAT-02 read tool <c>budget_status</c>: each category's limit vs live spend for a month. Wraps
/// <see cref="IBudgetReader.GetBudgetsForMonthAsync"/> (the derived-at-read-time budget picture, scoped
/// to the caller) and returns a compact rounded row per budget, including its BUD-03 state.
/// </summary>
public sealed partial class BudgetStatusTool(IBudgetReader reader) : IAssistantTool
{
    private static readonly LlmTool ToolSchemaValue = new(
        "budget_status",
        "Situação do orçamento do mês: limite vs gasto por categoria.",
        ToolSchema.Parse("""
        {
          "type": "object",
          "properties": {
            "month": { "type": "string", "description": "YYYY-MM" }
          },
          "required": ["month"],
          "additionalProperties": false
        }
        """));

    public string Name => "budget_status";
    public bool IsWrite => false;
    public LlmTool Schema => ToolSchemaValue;

    public async Task<string> ExecuteAsync(string argsJson, Guid userId, CancellationToken ct)
    {
        var args = JsonSerializer.Deserialize<Args>(argsJson) ?? new Args(null);

        if (string.IsNullOrWhiteSpace(args.Month) || !MonthPattern().IsMatch(args.Month))
        {
            return ToolResults.Error("invalid_month", "month deve ser no formato YYYY-MM.");
        }

        var year = int.Parse(args.Month[..4], CultureInfo.InvariantCulture);
        var month = int.Parse(args.Month[5..7], CultureInfo.InvariantCulture);
        if (month is < 1 or > 12)
        {
            return ToolResults.Error("invalid_month", "month deve ser no formato YYYY-MM.");
        }

        var result = await reader.GetBudgetsForMonthAsync(userId, year, month, ct);

        var items = result.Items.Select(b => new
        {
            category = b.CategoryName ?? "Total (global)",
            limit = Math.Round(b.LimitAmount, 2),
            spent = Math.Round(b.SpentAmount, 2),
            remaining = Math.Round(b.Remaining, 2),
            percentOfCap = Math.Round(b.PercentOfCap, 1),
            state = b.State.ToString(),
        });

        return JsonSerializer.Serialize(new
        {
            month = args.Month,
            currency = "BRL",
            items,
        });
    }

    [GeneratedRegex(@"^\d{4}-\d{2}$")]
    private static partial Regex MonthPattern();

    private sealed record Args([property: JsonPropertyName("month")] string? Month);
}
