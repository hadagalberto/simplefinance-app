using System.Text.Json;
using System.Text.Json.Serialization;
using SimpleFinance.Application.Ai;
using SimpleFinance.Application.Ai.Assistant;
using SimpleFinance.Infrastructure.NetWorth;

namespace SimpleFinance.Infrastructure.Ai.Assistant.Tools;

/// <summary>
/// AI-CHAT-02 read tool <c>net_worth_evolution</c>: the net-worth snapshot series over a range. Wraps
/// <see cref="INetWorthReader.GetEvolutionAsync"/> (which takes a day count). The <c>range</c> grammar
/// (<c>3m|6m|12m|ytd|all</c>) is mapped to a day count here; the result is a compact list of
/// date/value points (one per snapshot), capped so a long "all" history can never balloon the model
/// context.
/// </summary>
public sealed class NetWorthEvolutionTool(INetWorthReader reader) : IAssistantTool
{
    private const int MaxPoints = 60;

    private static readonly LlmTool ToolSchemaValue = new(
        "net_worth_evolution",
        "Evolução do patrimônio líquido ao longo do tempo.",
        ToolSchema.Parse("""
        {
          "type": "object",
          "properties": {
            "range": { "type": "string", "enum": ["3m", "6m", "12m", "ytd", "all"] }
          },
          "required": ["range"],
          "additionalProperties": false
        }
        """));

    public string Name => "net_worth_evolution";
    public bool IsWrite => false;
    public LlmTool Schema => ToolSchemaValue;

    public async Task<string> ExecuteAsync(string argsJson, Guid userId, CancellationToken ct)
    {
        var args = JsonSerializer.Deserialize<Args>(argsJson) ?? new Args(null);

        if (!TryResolveDays(args.Range, out var days))
        {
            return ToolResults.Error("invalid_range", "range deve ser um de: 3m, 6m, 12m, ytd, all.");
        }

        var evolution = await reader.GetEvolutionAsync(userId, days, ct);

        // Keep only the most-recent points so a long history stays compact for the model.
        var points = evolution.Points
            .OrderBy(p => p.Date)
            .TakeLast(MaxPoints)
            .Select(p => new { date = p.Date.ToString("yyyy-MM-dd"), netWorth = Math.Round(p.NetWorth, 2) });

        return JsonSerializer.Serialize(new
        {
            range = args.Range,
            currency = "BRL",
            points,
        });
    }

    private static bool TryResolveDays(string? range, out int days)
    {
        switch (range?.Trim().ToLowerInvariant())
        {
            case "3m": days = 90; return true;
            case "6m": days = 180; return true;
            case "12m": days = 365; return true;
            case "ytd": days = Math.Max(1, DateTime.UtcNow.DayOfYear); return true;
            case "all": days = 3650; return true; // ~10y — the snapshot series is short regardless
            default: days = 0; return false;
        }
    }

    private sealed record Args([property: JsonPropertyName("range")] string? Range);
}
