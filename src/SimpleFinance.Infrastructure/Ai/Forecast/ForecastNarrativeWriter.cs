using System.Text.Json;
using Microsoft.Extensions.Logging;
using SimpleFinance.Application.Ai;
using SimpleFinance.Application.Ai.Usage;
using SimpleFinance.Domain.Enums;
using ForecastEntity = SimpleFinance.Domain.Entities.Forecast;

namespace SimpleFinance.Infrastructure.Ai.Forecast;

/// <summary>
/// The skippable LLM narrative layer for the month forecast (19-02, AI-FCT-02). Turns the ALREADY-computed
/// deterministic <see cref="ForecastEntity"/> figures into a 1-2 sentence PT-BR <see cref="ForecastEntity.Narrative"/>
/// in ONE <see cref="ILlmClient.ChatAsync"/> call (<c>deepseek-chat</c>, <see cref="AiFeature.Forecast"/>),
/// modeled on <c>InsightProseWriter</c> but single-item.
///
/// The two invariants that make this safe (mirroring the insight prose layer):
/// <list type="bullet">
///   <item><b>Persistence never depends on the LLM.</b> The forecast row is built + persisted with its
///   deterministic numbers regardless; this writer only sets the optional prose. An
///   <see cref="AiBudgetExceededException"/> (over budget) or ANY other failure is swallowed and
///   <see cref="ForecastEntity.Narrative"/> is left null — <see cref="WriteAsync"/> never throws.</item>
///   <item><b>Grounding.</b> The model receives ONLY the pre-computed figures and is told to use them
///   verbatim; the authoritative numbers stay on the entity + <c>ByCategoryJson</c>, never sourced from the
///   model. The category names passed as facts are first-party (not PII), so no merchant free-text enters
///   the prompt (T-19-06).</item>
/// </list>
/// </summary>
public sealed class ForecastNarrativeWriter(
    ILlmClient llm,
    ILogger<ForecastNarrativeWriter> logger)
{
    private const int MaxNarrativeLen = 500;
    private const int TopDriversInPrompt = 3;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private const string SystemPrompt =
        "Você é um redator financeiro. Receberá NÚMEROS já calculados de uma projeção de fim de mês. " +
        "Escreva 1 a 2 frases em português do Brasil explicando a projeção. Regras: use EXATAMENTE os " +
        "números fornecidos; NUNCA invente, arredonde de forma diferente ou adicione dados; não dê conselhos " +
        "como se fossem fatos; responda apenas com o texto, sem JSON.";

    /// <summary>
    /// Attach a degrade-safe PT-BR narrative to <paramref name="forecast"/> in place. On a successful call
    /// <see cref="ForecastEntity.Narrative"/> is set (trimmed + truncated ≤500); over budget / any failure /
    /// an empty response leaves it null. Never throws — the caller persists the forecast either way. Cost is
    /// attributed to <see cref="AiFeature.Forecast"/>.
    /// </summary>
    public async Task WriteAsync(ForecastEntity forecast, string chatModel, CancellationToken ct)
    {
        try
        {
            var messages = new List<LlmMessage>
            {
                new("system", SystemPrompt),
                new("user", BuildFacts(forecast)),
            };

            var result = await llm.ChatAsync(new LlmChatRequest(chatModel, messages, AiFeature.Forecast), ct);
            var text = result.Content?.Trim();
            forecast.Narrative = string.IsNullOrWhiteSpace(text) ? null : Truncate(text, MaxNarrativeLen);
        }
        catch (AiBudgetExceededException)
        {
            // Expected degrade path — the forecast persists with its deterministic numbers, no narrative.
            forecast.Narrative = null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Forecast narrative generation failed; persisting without a narrative.");
            forecast.Narrative = null;
        }
    }

    private static string BuildFacts(ForecastEntity forecast)
    {
        var facts = new
        {
            projectedIncome = forecast.ProjectedIncome,
            projectedExpense = forecast.ProjectedExpense,
            projectedBalance = forecast.ProjectedBalance,
            balanceLow = forecast.BalanceLow,
            balanceHigh = forecast.BalanceHigh,
            topDrivers = ExtractTopDrivers(forecast.ByCategoryJson),
        };

        return JsonSerializer.Serialize(facts, JsonOptions);
    }

    // Pulls the top drivers (category + projected) from the authoritative ByCategoryJson so the model can
    // name what's driving the projection — numbers still come only from the deterministic blob.
    private static IReadOnlyList<object> ExtractTopDrivers(string byCategoryJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(byCategoryJson) ? "{}" : byCategoryJson);
            if (!doc.RootElement.TryGetProperty("drivers", out var drivers) || drivers.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var result = new List<object>();
            foreach (var d in drivers.EnumerateArray().Take(TopDriversInPrompt))
            {
                var category = d.TryGetProperty("category", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
                var projected = d.TryGetProperty("projected", out var p) && p.ValueKind == JsonValueKind.Number ? p.GetDecimal() : 0m;
                result.Add(new { category, projected });
            }

            return result;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
