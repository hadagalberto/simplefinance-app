using System.Text.Json;
using Microsoft.Extensions.Logging;
using SimpleFinance.Application.Ai;
using SimpleFinance.Application.Ai.Usage;
using SimpleFinance.Domain.Entities;
using SimpleFinance.Domain.Enums;

namespace SimpleFinance.Infrastructure.Ai.Insights;

/// <summary>
/// The skippable LLM phrasing layer for insights (18-03, AI-INS-01). Turns the deterministic
/// <see cref="InsightSignal"/> candidates from <see cref="IInsightDetector"/> into persistable
/// <see cref="AiInsight"/> rows, phrasing their PT-BR <c>Title</c>/<c>Body</c> in ONE batched, id-keyed
/// <see cref="ILlmClient.ChatAsync"/> call (chunked only when the run exceeds <see cref="MaxBatch"/>).
///
/// The two invariants that make this safe:
/// <list type="bullet">
///   <item><b>Persistence never depends on the LLM.</b> Every row is built up front with the detector's
///   deterministic <c>FallbackTitle</c>/<c>FallbackBody</c>; a successful call only OVERWRITES that prose.
///   An <see cref="AiBudgetExceededException"/> (over budget) or any other LLM/parse failure is swallowed
///   and the fallback is kept — <see cref="WriteAsync"/> never throws.</item>
///   <item><b>Grounding.</b> The model only ever supplies <c>Title</c>/<c>Body</c> strings; the
///   authoritative numbers stay in <see cref="AiInsight.DataJson"/>, copied verbatim from the signal and
///   never touched here, so a lying model can never move a number onto a card.</item>
/// </list>
/// Merchant/free-text carried in <c>Subject</c> is passed through <see cref="IPiiRedactor"/> before it
/// enters the prompt (Phase 17 convention / T-18-07), on top of the un-bypassable redaction the client
/// applies internally. Cost is attributed to <see cref="AiFeature.Insights"/>.
/// </summary>
public sealed class InsightProseWriter(
    ILlmClient llm,
    IPiiRedactor redactor,
    ILogger<InsightProseWriter> logger)
{
    /// <summary>Max signals per <see cref="ILlmClient.ChatAsync"/> call. The detector already caps a run at
    /// <see cref="InsightThresholds.MaxSignalsPerRun"/> (5), so in practice this is a single call; the chunk
    /// loop is defensive for any future higher cap.</summary>
    private const int MaxBatch = 10;

    private const int TitleMaxLength = 200;
    private const int BodyMaxLength = 1000;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private const string SystemPrompt =
        "Você é um redator financeiro. Receberá FATOS já calculados (números exatos). " +
        "Para cada item, escreva um \"title\" curto (até 60 caracteres) e um \"body\" de 1 a 2 frases " +
        "(até 240 caracteres) em português do Brasil. Regras: use EXATAMENTE os números fornecidos; " +
        "NUNCA invente, arredonde de forma diferente ou adicione dados; não dê conselhos financeiros; " +
        "responda SOMENTE com JSON no formato " +
        "{\"items\":[{\"id\":\"...\",\"title\":\"...\",\"body\":\"...\"}]}, mantendo o mesmo \"id\" recebido.";

    /// <summary>
    /// Build a persistable <see cref="AiInsight"/> per signal (Status=New, deterministic fallback prose),
    /// then batch-phrase their Title/Body via one id-keyed <see cref="ILlmClient.ChatAsync"/> call. On
    /// success the matching row's Title/Body are overwritten; unmatched ids and any failure keep the
    /// deterministic fallback. The caller sets <see cref="AiInsight.UserId"/> and Upserts each row. Cost is
    /// attributed to <see cref="AiFeature.Insights"/>.
    /// </summary>
    public Task<IReadOnlyList<AiInsight>> WriteAsync(
        IReadOnlyList<InsightSignal> signals, string chatModel, CancellationToken ct) =>
        WriteAsync(signals, chatModel, AiFeature.Insights, ct);

    /// <summary>
    /// Same as <see cref="WriteAsync(IReadOnlyList{InsightSignal}, string, CancellationToken)"/> but attributes
    /// the LLM cost to <paramref name="feature"/> — 18-05's <c>AiAnomalyJob</c> passes
    /// <see cref="AiFeature.Anomaly"/> so anomaly phrasing is billed separately from insight phrasing.
    /// </summary>
    public async Task<IReadOnlyList<AiInsight>> WriteAsync(
        IReadOnlyList<InsightSignal> signals, string chatModel, AiFeature feature, CancellationToken ct)
    {
        // Deterministic-first: build every row with the fallback prose BEFORE any LLM call.
        var insights = signals.Select(ToInsight).ToList();
        if (insights.Count == 0)
        {
            return insights;
        }

        for (var offset = 0; offset < signals.Count; offset += MaxBatch)
        {
            var chunk = signals.Skip(offset).Take(MaxBatch).ToList();
            var prose = await TryPhraseAsync(chunk, chatModel, feature, ct);
            if (prose is null)
            {
                continue; // over budget / failed / unparseable — keep this chunk's fallback prose
            }

            foreach (var (id, item) in prose)
            {
                var target = insights.FirstOrDefault(i => i.DedupKey == id);
                if (target is null)
                {
                    continue; // model answered an id we never sent — ignore
                }

                if (!string.IsNullOrWhiteSpace(item.Title))
                {
                    target.Title = Truncate(item.Title!, TitleMaxLength);
                }

                if (!string.IsNullOrWhiteSpace(item.Body))
                {
                    target.Body = Truncate(item.Body!, BodyMaxLength);
                }
            }
        }

        return insights;
    }

    private async Task<Dictionary<string, ProseItem>?> TryPhraseAsync(
        IReadOnlyList<InsightSignal> chunk, string chatModel, AiFeature feature, CancellationToken ct)
    {
        try
        {
            var messages = new List<LlmMessage>
            {
                new("system", SystemPrompt),
                new("user", BuildFacts(chunk)),
            };

            var result = await llm.ChatAsync(
                new LlmChatRequest(chatModel, messages, feature), ct);

            return ParseProse(result.Content);
        }
        catch (AiBudgetExceededException)
        {
            // Expected degrade path — persistence must not depend on the LLM.
            return null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Insight prose generation failed; using deterministic fallback for {Count} signal(s).",
                chunk.Count);
            return null;
        }
    }

    private string BuildFacts(IReadOnlyList<InsightSignal> chunk)
    {
        var items = chunk.Select(s => new
        {
            id = s.Id,
            kind = s.Kind.ToString(),
            subject = redactor.Redact(s.Subject),
            data = ParseData(s.DataJson),
        });

        return JsonSerializer.Serialize(new { items }, JsonOptions);
    }

    private static JsonElement ParseData(string dataJson)
    {
        try
        {
            return JsonSerializer.Deserialize<JsonElement>(
                string.IsNullOrWhiteSpace(dataJson) ? "{}" : dataJson, JsonOptions);
        }
        catch (JsonException)
        {
            return JsonSerializer.Deserialize<JsonElement>("{}", JsonOptions);
        }
    }

    private static Dictionary<string, ProseItem>? ParseProse(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        using var doc = JsonDocument.Parse(ExtractJsonObject(content));
        if (!doc.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var map = new Dictionary<string, ProseItem>();
        foreach (var el in items.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object
                || !el.TryGetProperty("id", out var idEl)
                || idEl.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var id = idEl.GetString();
            if (string.IsNullOrEmpty(id))
            {
                continue;
            }

            var title = el.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            var body = el.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() : null;
            map[id] = new ProseItem(title, body);
        }

        return map;
    }

    /// <summary>Tolerate a model that wraps its JSON in prose or a ```json fence by taking the outermost
    /// object span. Any residual malformed content throws in <see cref="JsonDocument.Parse(string)"/> and is
    /// caught upstream (degrade to fallback).</summary>
    private static string ExtractJsonObject(string content)
    {
        var start = content.IndexOf('{');
        var end = content.LastIndexOf('}');
        return start >= 0 && end > start ? content[start..(end + 1)] : content;
    }

    private static AiInsight ToInsight(InsightSignal s) => new()
    {
        Kind = s.Kind,
        Severity = s.Severity,
        Title = Truncate(s.FallbackTitle, TitleMaxLength),
        Body = Truncate(s.FallbackBody, BodyMaxLength),
        PeriodStart = s.PeriodStart,
        PeriodEnd = s.PeriodEnd,
        DataJson = s.DataJson,
        DedupKey = s.DedupKey,
        Status = AiInsightStatus.New,
    };

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];

    private sealed record ProseItem(string? Title, string? Body);
}
