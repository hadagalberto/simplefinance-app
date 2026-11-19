using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using SimpleFinance.Application.Ai;
using SimpleFinance.Application.Ai.Usage;
using SimpleFinance.Domain.Enums;
using SimpleFinance.Infrastructure.Ai;
using SimpleFinance.Infrastructure.Ai.Insights;

namespace SimpleFinance.UnitTests.Ai;

/// <summary>
/// Behavior tests for <see cref="InsightProseWriter"/> (18-03 Task 1, AI-INS-01). Proves the ONE batched,
/// id-keyed <see cref="ILlmClient.ChatAsync"/> call phrases every signal's Title/Body, that an
/// <see cref="AiBudgetExceededException"/> (or any LLM/parse failure) degrades to the deterministic
/// fallback WITHOUT throwing, and — the grounding invariant — that <c>DataJson</c> always carries the
/// detector's numbers regardless of what the model returns.
/// </summary>
public class InsightProseWriterTests
{
    private static InsightSignal Signal(
        string id,
        decimal currentAmount,
        string fallbackTitle = "titulo padrao",
        string fallbackBody = "corpo padrao",
        AiInsightKind kind = AiInsightKind.SpendingChange,
        string subject = "Alimentação") =>
        new(
            id,
            kind,
            AiInsightSeverity.Info,
            subject,
            new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 7, 31, 23, 59, 59, DateTimeKind.Utc),
            $"{{\"currentAmount\":{currentAmount.ToString(CultureInfo.InvariantCulture)}}}",
            id,
            fallbackTitle,
            fallbackBody);

    private static InsightProseWriter Build(ScriptedLlmClient llm) =>
        new(llm, new PiiRedactor(), NullLogger<InsightProseWriter>.Instance);

    private static string ItemsJson(params (string id, string title, string body)[] items) =>
        JsonSerializer.Serialize(new
        {
            items = items.Select(i => new { id = i.id, title = i.title, body = i.body }),
        });

    // (a) success path overwrites Title/Body by id -----------------------------------------------

    [Fact]
    public async Task WriteAsync_Success_OverwritesTitleAndBodyById()
    {
        var s1 = Signal("id-1", 260m, "fallback t1", "fallback b1");
        var s2 = Signal("id-2", 90m, "fallback t2", "fallback b2");
        var llm = new ScriptedLlmClient
        {
            OnChat = _ => ItemsJson(
                ("id-1", "Alimentação cara", "Você gastou mais este mês."),
                ("id-2", "Lazer alto", "Seu lazer subiu.")),
        };

        var result = await Build(llm).WriteAsync([s1, s2], "deepseek-chat", CancellationToken.None);

        var r1 = result.Single(x => x.DedupKey == "id-1");
        var r2 = result.Single(x => x.DedupKey == "id-2");
        Assert.Equal("Alimentação cara", r1.Title);
        Assert.Equal("Você gastou mais este mês.", r1.Body);
        Assert.Equal("Lazer alto", r2.Title);
        Assert.Equal("Seu lazer subiu.", r2.Body);
        Assert.Equal(AiInsightStatus.New, r1.Status);
    }

    [Fact]
    public async Task WriteAsync_UnmatchedId_KeepsDeterministicFallback()
    {
        var s1 = Signal("id-1", 260m, "fallback t1", "fallback b1");
        var llm = new ScriptedLlmClient
        {
            // Model answers about an id we never sent — the real signal must keep its fallback.
            OnChat = _ => ItemsJson(("other-id", "wrong", "wrong")),
        };

        var result = await Build(llm).WriteAsync([s1], "deepseek-chat", CancellationToken.None);

        var r1 = Assert.Single(result);
        Assert.Equal("fallback t1", r1.Title);
        Assert.Equal("fallback b1", r1.Body);
    }

    // (b) budget-exceeded keeps fallback + no throw ----------------------------------------------

    [Fact]
    public async Task WriteAsync_BudgetExceeded_KeepsFallback_NeverThrows()
    {
        var s1 = Signal("id-1", 260m, "fallback t1", "fallback b1");
        var llm = new ScriptedLlmClient
        {
            ThrowOnChat = new AiBudgetExceededException(Guid.NewGuid(), 5m, 5m),
        };

        var result = await Build(llm).WriteAsync([s1], "deepseek-chat", CancellationToken.None);

        var r1 = Assert.Single(result);
        Assert.Equal("fallback t1", r1.Title);
        Assert.Equal("fallback b1", r1.Body);
    }

    [Fact]
    public async Task WriteAsync_LlmThrowsGeneric_KeepsFallback_NeverThrows()
    {
        var s1 = Signal("id-1", 260m, "fallback t1", "fallback b1");
        var llm = new ScriptedLlmClient { ThrowOnChat = new HttpRequestException("boom") };

        var result = await Build(llm).WriteAsync([s1], "deepseek-chat", CancellationToken.None);

        Assert.Equal("fallback t1", Assert.Single(result).Title);
    }

    [Fact]
    public async Task WriteAsync_UnparseableResponse_KeepsFallback_NeverThrows()
    {
        var s1 = Signal("id-1", 260m, "fallback t1", "fallback b1");
        var llm = new ScriptedLlmClient { OnChat = _ => "this is not json at all" };

        var result = await Build(llm).WriteAsync([s1], "deepseek-chat", CancellationToken.None);

        Assert.Equal("fallback t1", Assert.Single(result).Title);
    }

    // (c) grounding — DataJson unchanged regardless of a lying model ------------------------------

    [Fact]
    public async Task WriteAsync_ModelReturnsBogusNumbers_DataJsonStaysDetectorNumbers()
    {
        var s1 = Signal("id-1", 260m, "fallback t1", "fallback b1");
        var llm = new ScriptedLlmClient
        {
            // The model invents a wildly wrong number in the prose body.
            OnChat = _ => ItemsJson(("id-1", "Gasto de R$ 999999", "Você gastou R$ 999999,00 este mês.")),
        };

        var result = await Build(llm).WriteAsync([s1], "deepseek-chat", CancellationToken.None);

        var r1 = Assert.Single(result);
        // Prose is applied to Title/Body...
        Assert.Equal("Gasto de R$ 999999", r1.Title);
        // ...but the authoritative numbers survive verbatim.
        Assert.Equal(s1.DataJson, r1.DataJson);
        Assert.Equal(260m, JsonDocument.Parse(r1.DataJson).RootElement.GetProperty("currentAmount").GetDecimal());
    }

    // (d) exactly one ChatAsync call for N signals -----------------------------------------------

    [Fact]
    public async Task WriteAsync_ManySignals_IssuesExactlyOneBatchedCall()
    {
        var signals = Enumerable.Range(0, 5)
            .Select(i => Signal($"id-{i}", 100m + i))
            .ToList();
        var llm = new ScriptedLlmClient { OnChat = _ => ItemsJson() };

        await Build(llm).WriteAsync(signals, "deepseek-chat", CancellationToken.None);

        Assert.Equal(1, llm.ChatCallCount);
    }

    [Fact]
    public async Task WriteAsync_UsesInsightsFeatureAndConfiguredModel()
    {
        var s1 = Signal("id-1", 260m);
        var llm = new ScriptedLlmClient { OnChat = _ => ItemsJson(("id-1", "t", "b")) };

        await Build(llm).WriteAsync([s1], "my-model", CancellationToken.None);

        Assert.NotNull(llm.LastRequest);
        Assert.Equal("my-model", llm.LastRequest!.Model);
        Assert.Equal(AiFeature.Insights, llm.LastRequest.Feature);
    }

    [Fact]
    public async Task WriteAsync_NoSignals_ReturnsEmpty_NoLlmCall()
    {
        var llm = new ScriptedLlmClient();

        var result = await Build(llm).WriteAsync([], "deepseek-chat", CancellationToken.None);

        Assert.Empty(result);
        Assert.Equal(0, llm.ChatCallCount);
    }

    // ---- fake ----------------------------------------------------------------------------------

    private sealed class ScriptedLlmClient : ILlmClient
    {
        public Func<LlmChatRequest, string>? OnChat { get; set; }

        public Exception? ThrowOnChat { get; set; }

        public int ChatCallCount { get; private set; }

        public LlmChatRequest? LastRequest { get; private set; }

        public Task<LlmResult> ChatAsync(LlmChatRequest request, CancellationToken ct)
        {
            ChatCallCount++;
            LastRequest = request;
            if (ThrowOnChat is not null)
            {
                throw ThrowOnChat;
            }

            var content = OnChat?.Invoke(request) ?? "{}";
            return Task.FromResult(new LlmResult(content, null, [], new TokenUsage(10, 5)));
        }

        public async IAsyncEnumerable<LlmStreamChunk> ChatStreamAsync(
            LlmChatRequest request,
            [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            throw new NotSupportedException();
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }
    }
}
