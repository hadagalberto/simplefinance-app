using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SimpleFinance.Application.Ai;
using SimpleFinance.Application.Ai.Usage;
using SimpleFinance.Domain.Entities;
using SimpleFinance.Domain.Enums;
using SimpleFinance.Infrastructure.Ai;
using SimpleFinance.Infrastructure.Ai.DeepSeek;
using SimpleFinance.Infrastructure.Persistence;

namespace SimpleFinance.UnitTests.Ai;

/// <summary>
/// AI-FND-01 (15-05): the concrete DeepSeek chat client. Proven with a fake
/// <see cref="HttpMessageHandler"/> (captures the outgoing body, returns canned JSON) so no key/network
/// is needed. Covers the un-bypassable invariant ordering redact → budget → send → record: PII never
/// reaches the wire body (real <see cref="PiiRedactor"/>), a throwing budget guard makes ZERO HTTP
/// calls, usage is recorded once after a successful response, and the model travels from the request.
/// </summary>
public class DeepSeekLlmClientTests
{
    private const string CannedResponseJson = """
        {
          "choices": [
            { "index": 0, "message": { "content": "Olá, tudo bem?", "reasoning_content": null, "tool_calls": null }, "finish_reason": "stop" }
          ],
          "usage": { "prompt_tokens": 42, "completion_tokens": 7, "total_tokens": 49 }
        }
        """;

    /// <summary>Captures the request body and counts calls; returns a fixed canned response.</summary>
    private sealed class CapturingHandler(string responseJson) : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        public string? LastRequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            LastRequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json"),
            };
        }
    }

    /// <summary>Returns an HttpClient over a single handler for any client name (streaming path).</summary>
    private sealed class SingleClientFactory(HttpMessageHandler handler, Uri baseAddress) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false) { BaseAddress = baseAddress };
    }

    private sealed class PassThroughBudgetGuard : IAiBudgetGuard
    {
        public Task EnsureWithinBudgetAsync(Guid userId, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class ThrowingBudgetGuard : IAiBudgetGuard
    {
        public Task EnsureWithinBudgetAsync(Guid userId, CancellationToken ct)
            => throw new AiBudgetExceededException(userId, currentSpendUsd: 5m, monthlyBudgetUsd: 3m);
    }

    private sealed class RecordingUsageRecorder : IAiUsageRecorder
    {
        public int CallCount { get; private set; }

        public Guid LastUserId { get; private set; }

        public AiFeature LastFeature { get; private set; }

        public AiProvider LastProvider { get; private set; }

        public string? LastModel { get; private set; }

        public int LastPromptTokens { get; private set; }

        public int LastCompletionTokens { get; private set; }

        public Task RecordAsync(Guid userId, AiFeature feature, AiProvider provider, string model, int promptTokens, int completionTokens, CancellationToken ct)
        {
            CallCount++;
            LastUserId = userId;
            LastFeature = feature;
            LastProvider = provider;
            LastModel = model;
            LastPromptTokens = promptTokens;
            LastCompletionTokens = completionTokens;
            return Task.CompletedTask;
        }
    }

    private static SimpleFinanceDbContext CreateDbWithUser(out Guid userId)
    {
        var db = new SimpleFinanceDbContext(new DbContextOptionsBuilder<SimpleFinanceDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options);
        userId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        db.Users.Add(new User { Id = userId, Name = "Owner", Email = "owner@example.com", PasswordHash = "x", CreatedAt = now, UpdatedAt = now });
        db.SaveChanges();
        return db;
    }

    private static IPiiRedactor RealRedactor() => new PiiRedactor();

    private static IOptions<AiOptions> Options() => Microsoft.Extensions.Options.Options.Create(new AiOptions
    {
        DeepSeekApiKey = "test-key",
        DeepSeek = new AiProviderEndpoint { BaseUrl = "https://api.deepseek.com" },
    });

    private static DeepSeekLlmClient CreateClient(
        CapturingHandler handler,
        SimpleFinanceDbContext db,
        IAiBudgetGuard guard,
        IAiUsageRecorder recorder,
        IPiiRedactor? redactor = null)
    {
        var baseAddress = new Uri("https://api.deepseek.com");
        var httpClient = new HttpClient(handler, disposeHandler: false) { BaseAddress = baseAddress };
        var factory = new SingleClientFactory(handler, baseAddress);
        return new DeepSeekLlmClient(httpClient, factory, Options(), redactor ?? RealRedactor(), guard, recorder, db);
    }

    private static LlmChatRequest Request(string content, string model = "deepseek-chat") => new(
        Model: model,
        Messages: [new LlmMessage(Role: "user", Content: content)],
        Feature: AiFeature.Assistant);

    [Fact]
    public async Task ChatAsync_ReturnsContentAndUsage_AndRecordsUsageOnce()
    {
        using var handler = new CapturingHandler(CannedResponseJson);
        await using var db = CreateDbWithUser(out var userId);
        var recorder = new RecordingUsageRecorder();
        var client = CreateClient(handler, db, new PassThroughBudgetGuard(), recorder);

        var result = await client.ChatAsync(Request("Bom dia"), CancellationToken.None);

        Assert.Equal("Olá, tudo bem?", result.Content);
        Assert.Equal(42, result.Usage.PromptTokens);
        Assert.Equal(7, result.Usage.CompletionTokens);
        Assert.Equal(1, recorder.CallCount);
        Assert.Equal(userId, recorder.LastUserId);
        Assert.Equal("deepseek-chat", recorder.LastModel);
        Assert.Equal(AiProvider.DeepSeek, recorder.LastProvider);
        Assert.Equal(AiFeature.Assistant, recorder.LastFeature);
        Assert.Equal(42, recorder.LastPromptTokens);
        Assert.Equal(7, recorder.LastCompletionTokens);
    }

    [Fact]
    public async Task ChatAsync_RedactsCpf_BeforeSerialization_SoItNeverReachesTheWireBody()
    {
        using var handler = new CapturingHandler(CannedResponseJson);
        await using var db = CreateDbWithUser(out _);
        var client = CreateClient(handler, db, new PassThroughBudgetGuard(), new RecordingUsageRecorder());

        await client.ChatAsync(Request("Pagamento para CPF 123.456.789-09 confirmado"), CancellationToken.None);

        Assert.NotNull(handler.LastRequestBody);
        Assert.DoesNotContain("123.456.789", handler.LastRequestBody);
        Assert.DoesNotContain("12345678909", handler.LastRequestBody);
        Assert.Contains("REDACTED", handler.LastRequestBody);
    }

    [Fact]
    public async Task ChatAsync_WhenBudgetGuardThrows_MakesZeroHttpCalls()
    {
        using var handler = new CapturingHandler(CannedResponseJson);
        await using var db = CreateDbWithUser(out _);
        var recorder = new RecordingUsageRecorder();
        var client = CreateClient(handler, db, new ThrowingBudgetGuard(), recorder);

        await Assert.ThrowsAsync<AiBudgetExceededException>(
            () => client.ChatAsync(Request("qualquer coisa"), CancellationToken.None));

        Assert.Equal(0, handler.CallCount);
        Assert.Equal(0, recorder.CallCount);
    }

    [Fact]
    public async Task ChatAsync_SendsModelFromRequest_NotAHardcodedConstant()
    {
        using var handler = new CapturingHandler(CannedResponseJson);
        await using var db = CreateDbWithUser(out _);
        var client = CreateClient(handler, db, new PassThroughBudgetGuard(), new RecordingUsageRecorder());

        await client.ChatAsync(Request("olá", model: "deepseek-reasoner"), CancellationToken.None);

        Assert.NotNull(handler.LastRequestBody);
        Assert.Contains("deepseek-reasoner", handler.LastRequestBody);
    }

    [Fact]
    public async Task ChatStreamAsync_WhenBudgetGuardThrows_MakesZeroHttpCalls()
    {
        using var handler = new CapturingHandler(CannedResponseJson);
        await using var db = CreateDbWithUser(out _);
        var client = CreateClient(handler, db, new ThrowingBudgetGuard(), new RecordingUsageRecorder());

        await Assert.ThrowsAsync<AiBudgetExceededException>(async () =>
        {
            await foreach (var _ in client.ChatStreamAsync(Request("stream"), CancellationToken.None))
            {
            }
        });

        Assert.Equal(0, handler.CallCount);
    }
}
