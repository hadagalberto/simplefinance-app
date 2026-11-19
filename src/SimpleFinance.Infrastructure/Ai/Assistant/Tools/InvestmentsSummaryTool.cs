using System.Text.Json;
using SimpleFinance.Application.Ai;
using SimpleFinance.Application.Ai.Assistant;
using SimpleFinance.Infrastructure.Investments;

namespace SimpleFinance.Infrastructure.Ai.Assistant.Tools;

/// <summary>
/// AI-CHAT-02 read tool <c>investments_summary</c>: totals + gain + distribution by type/issuer. Wraps
/// <see cref="IInvestmentsReader.GetSummaryAsync"/>, scoped to the caller, and returns a compact rounded
/// summary. Describes positions only — never investment advice (AI-CHAT-05, enforced by the system
/// prompt in 17-04).
/// </summary>
public sealed class InvestmentsSummaryTool(IInvestmentsReader reader) : IAssistantTool
{
    private static readonly LlmTool ToolSchemaValue = new(
        "investments_summary",
        "Resumo dos investimentos: total investido, valor atual, ganho e distribuição por tipo/emissor.",
        ToolSchema.Parse("""
        {
          "type": "object",
          "properties": {},
          "additionalProperties": false
        }
        """));

    public string Name => "investments_summary";
    public bool IsWrite => false;
    public LlmTool Schema => ToolSchemaValue;

    public async Task<string> ExecuteAsync(string argsJson, Guid userId, CancellationToken ct)
    {
        var summary = await reader.GetSummaryAsync(userId, ct);

        return JsonSerializer.Serialize(new
        {
            currency = "BRL",
            totalInvested = Math.Round(summary.TotalInvested, 2),
            totalCurrentValue = Math.Round(summary.TotalCurrentValue, 2),
            totalGain = Math.Round(summary.TotalGain, 2),
            totalGainPercent = summary.TotalGainPercent is null ? (decimal?)null : Math.Round(summary.TotalGainPercent.Value, 2),
            byType = summary.ByType.Select(d => new { label = d.Label, value = Math.Round(d.Value, 2), percent = Math.Round(d.Percent, 2) }),
            byIssuer = summary.ByIssuer.Select(d => new { label = d.Label, value = Math.Round(d.Value, 2), percent = Math.Round(d.Percent, 2) }),
        });
    }
}
