using System.Text.Json;
using System.Text.Json.Serialization;
using SimpleFinance.Application.Ai;
using SimpleFinance.Application.Ai.Assistant;
using SimpleFinance.Infrastructure.Recurring;

namespace SimpleFinance.Infrastructure.Ai.Assistant.Tools;

/// <summary>
/// AI-CHAT-02 read tool <c>list_recurring</c>: detected recurring charges / subscriptions plus the
/// monthly committed cost. Wraps <see cref="RecurringService.GetRecurringAsync"/> (EF-backed, scoped to
/// the caller) and returns a compact rounded top-20. Each item's display name is PII-redacted before it
/// enters the model context (the name is often merchant-derived).
/// </summary>
public sealed class ListRecurringTool(RecurringService recurringService, IPiiRedactor redactor) : IAssistantTool
{
    private const int MaxItems = 20;

    private static readonly LlmTool ToolSchemaValue = new(
        "list_recurring",
        "Lista de cobranças recorrentes/assinaturas e o custo mensal comprometido.",
        ToolSchema.Parse("""
        {
          "type": "object",
          "properties": {
            "activeOnly": { "type": "boolean", "description": "Se true, retorna apenas recorrências ativas." }
          },
          "additionalProperties": false
        }
        """));

    public string Name => "list_recurring";
    public bool IsWrite => false;
    public LlmTool Schema => ToolSchemaValue;

    public async Task<string> ExecuteAsync(string argsJson, Guid userId, CancellationToken ct)
    {
        var args = JsonSerializer.Deserialize<Args>(argsJson) ?? new Args(null);

        var result = await recurringService.GetRecurringAsync(userId, ct);

        var rows = result.Items.AsEnumerable();
        if (args.ActiveOnly == true)
        {
            rows = rows.Where(r => r.IsActive);
        }

        var items = rows
            .Take(MaxItems)
            .Select(r => new
            {
                name = redactor.Redact(r.DisplayName),
                amount = Math.Round(r.AverageAmount, 2),
                frequency = r.Frequency,
                category = r.CategoryName,
                isActive = r.IsActive,
                isSubscription = r.IsSubscription,
            })
            .ToList();

        return JsonSerializer.Serialize(new
        {
            currency = "BRL",
            monthlyCommittedCost = Math.Round(result.MonthlyCommittedCost, 2),
            count = items.Count,
            items,
        });
    }

    private sealed record Args([property: JsonPropertyName("activeOnly")] bool? ActiveOnly);
}
