using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SimpleFinance.Application.Ai;
using SimpleFinance.Application.Ai.Usage;
using SimpleFinance.Domain.Entities;
using SimpleFinance.Domain.Enums;

namespace SimpleFinance.Infrastructure.Ai.Insights;

/// <summary>
/// The skippable <c>deepseek-reasoner</c> summary layer for the monthly summary (19-03, AI-FCT-03). Turns the
/// ALREADY-computed deterministic <see cref="MonthlyFactPackage"/> into a PT-BR summary (3-5 destaques + 2-3
/// sugestões) in ONE <see cref="ILlmClient.ChatAsync"/> call (<see cref="AiFeature.MonthlySummary"/>),
/// modeled on <c>InsightProseWriter</c> but single-item. The reasoner is the ONE premium call this project
/// makes (once/month, "caro→barato").
///
/// The invariants that make this safe:
/// <list type="bullet">
///   <item><b>Persistence never depends on the LLM.</b> The caller builds the <see cref="AiInsight"/> with a
///   deterministic fallback Title/Body first; this writer only OVERWRITES that prose on success. An
///   <see cref="AiBudgetExceededException"/> (over budget) or ANY other failure/parse error is swallowed and
///   the fallback is kept — <see cref="WriteAsync"/> never throws.</item>
///   <item><b>Content-only (RESEARCH Pitfall 4 / T-19-11).</b> Only <see cref="LlmResult.Content"/> is read;
///   the reasoner's chain-of-thought in <see cref="LlmResult.ReasoningContent"/> is NEVER read, persisted, or
///   echoed. This is a single-shot call, so no <c>reasoning_content</c> is ever fed back.</item>
///   <item><b>Grounding.</b> The model only supplies prose; the authoritative numbers stay in the fact
///   package / <see cref="AiInsight.DataJson"/>, never sourced from the model.</item>
/// </list>
/// </summary>
public sealed class MonthlySummarizer(
    ILlmClient llm,
    ILogger<MonthlySummarizer> logger)
{
    private const int TitleMaxLength = 200;
    private const int BodyMaxLength = 1000;

    private const string SystemPrompt =
        "Você é um analista financeiro. Receberá um PACOTE DE FATOS já calculados do mês anterior. " +
        "Produza um resumo em português do Brasil com 3 a 5 destaques (o que mais mudou / chamou atenção) e " +
        "2 a 3 sugestões acionáveis. Regras: use EXATAMENTE os números fornecidos; NUNCA invente ou recalcule " +
        "valores; responda SOMENTE com JSON no formato " +
        "{\"title\":\"...\",\"highlights\":[\"...\"],\"suggestions\":[\"...\"]}.";

    /// <summary>
    /// Overwrite <paramref name="insight"/>'s Title/Body with a reasoner-authored PT-BR summary over
    /// <paramref name="factJson"/>. On a successful, parseable call the title (≤200) and body (destaques +
    /// sugestões joined, ≤1000) are set; over budget / any failure / an unparseable response leaves the
    /// caller's deterministic fallback untouched. Never throws. Reads <see cref="LlmResult.Content"/> ONLY —
    /// <see cref="LlmResult.ReasoningContent"/> is never touched. Cost is attributed to
    /// <see cref="AiFeature.MonthlySummary"/>.
    /// </summary>
    public async Task WriteAsync(AiInsight insight, string factJson, string summaryModel, CancellationToken ct)
    {
        try
        {
            var messages = new List<LlmMessage>
            {
                new("system", SystemPrompt),
                new("user", factJson),
            };

            var result = await llm.ChatAsync(new LlmChatRequest(summaryModel, messages, AiFeature.MonthlySummary), ct);

            // Content ONLY — NEVER result.ReasoningContent (Pitfall 4 / T-19-11).
            var parsed = ParseSummary(result.Content);
            if (parsed is null)
            {
                return; // unparseable — keep the deterministic fallback
            }

            if (!string.IsNullOrWhiteSpace(parsed.Title))
            {
                insight.Title = Truncate(parsed.Title!.Trim(), TitleMaxLength);
            }

            var body = ComposeBody(parsed);
            if (!string.IsNullOrWhiteSpace(body))
            {
                insight.Body = Truncate(body, BodyMaxLength);
            }
        }
        catch (AiBudgetExceededException)
        {
            // Expected degrade path — the summary persists with its deterministic fallback prose.
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Monthly summary generation failed; persisting the deterministic fallback.");
        }
    }

    private static string ComposeBody(SummaryDoc doc)
    {
        var sb = new StringBuilder();
        foreach (var h in doc.Highlights.Where(h => !string.IsNullOrWhiteSpace(h)))
        {
            sb.Append("• ").Append(h!.Trim()).Append('\n');
        }

        var suggestions = doc.Suggestions.Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
        if (suggestions.Count > 0)
        {
            sb.Append("\nSugestões:\n");
            foreach (var s in suggestions)
            {
                sb.Append("• ").Append(s!.Trim()).Append('\n');
            }
        }

        return sb.ToString().Trim();
    }

    private static SummaryDoc? ParseSummary(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(ExtractJsonObject(content));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var title = root.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            var highlights = ReadStringArray(root, "highlights");
            var suggestions = ReadStringArray(root, "suggestions");
            return new SummaryDoc(title, highlights, suggestions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static IReadOnlyList<string?> ReadStringArray(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var list = new List<string?>();
        foreach (var el in arr.EnumerateArray())
        {
            if (el.ValueKind == JsonValueKind.String)
            {
                list.Add(el.GetString());
            }
        }

        return list;
    }

    /// <summary>Tolerate a model that wraps its JSON in prose or a ```json fence by taking the outermost
    /// object span (mirrors <c>InsightProseWriter.ExtractJsonObject</c>).</summary>
    private static string ExtractJsonObject(string content)
    {
        var start = content.IndexOf('{');
        var end = content.LastIndexOf('}');
        return start >= 0 && end > start ? content[start..(end + 1)] : content;
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

    private sealed record SummaryDoc(string? Title, IReadOnlyList<string?> Highlights, IReadOnlyList<string?> Suggestions);
}
