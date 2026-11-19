using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using SimpleFinance.Application.Ai.Usage;
using SimpleFinance.Domain.Entities;
using SimpleFinance.Domain.Enums;
using SimpleFinance.Infrastructure.Ai;
using SimpleFinance.Infrastructure.Persistence;

namespace SimpleFinance.UnitTests.Ai;

/// <summary>
/// Shared fakes for the Anthropic / OpenRouter / routing client tests (the DeepSeek tests keep their own
/// private copies unchanged). A capturing handler returns canned bodies so no key or network is needed.
/// </summary>
internal static class ProviderClientTestSupport
{
    internal sealed class CapturingHandler(string responseBody, string mediaType = "application/json") : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        public string? LastRequestBody { get; private set; }

        public HttpRequestMessage? LastRequest { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            LastRequest = request;
            LastRequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, mediaType),
            };
        }
    }

    internal sealed class SingleClientFactory(HttpMessageHandler handler, Uri? baseAddress = null) : IHttpClientFactory
    {
        public List<string> RequestedNames { get; } = [];

        public HttpClient CreateClient(string name)
        {
            RequestedNames.Add(name);
            var client = new HttpClient(handler, disposeHandler: false);
            if (baseAddress is not null)
            {
                client.BaseAddress = baseAddress;
            }

            return client;
        }
    }

    internal sealed class PassThroughBudgetGuard : IAiBudgetGuard
    {
        public Task EnsureWithinBudgetAsync(Guid userId, CancellationToken ct) => Task.CompletedTask;
    }

    internal sealed class ThrowingBudgetGuard : IAiBudgetGuard
    {
        public Task EnsureWithinBudgetAsync(Guid userId, CancellationToken ct)
            => throw new AiBudgetExceededException(userId, currentSpendUsd: 5m, monthlyBudgetUsd: 3m);
    }

    internal sealed class RecordingUsageRecorder : IAiUsageRecorder
    {
        public int CallCount { get; private set; }

        public AiProvider LastProvider { get; private set; }

        public string? LastModel { get; private set; }

        public int LastPromptTokens { get; private set; }

        public int LastCompletionTokens { get; private set; }

        public Task RecordAsync(Guid userId, AiFeature feature, AiProvider provider, string model, int promptTokens, int completionTokens, CancellationToken ct)
        {
            CallCount++;
            LastProvider = provider;
            LastModel = model;
            LastPromptTokens = promptTokens;
            LastCompletionTokens = completionTokens;
            return Task.CompletedTask;
        }
    }

    internal sealed class StubKeyProvider(string? key) : IAiKeyProvider
    {
        public AiProvider? LastRequested { get; private set; }

        public Task<string?> GetKeyAsync(AiProvider provider, CancellationToken ct)
        {
            LastRequested = provider;
            return Task.FromResult(key);
        }

        public Task<string?> GetDeepSeekKeyAsync(CancellationToken ct) => GetKeyAsync(AiProvider.DeepSeek, ct);

        public Task<string?> GetOpenAiKeyAsync(CancellationToken ct) => GetKeyAsync(AiProvider.OpenAi, ct);
    }

    internal static SimpleFinanceDbContext CreateDbWithUser()
    {
        var db = new SimpleFinanceDbContext(new DbContextOptionsBuilder<SimpleFinanceDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options);
        var now = DateTime.UtcNow;
        db.Users.Add(new User { Id = Guid.NewGuid(), Name = "Owner", Email = "owner-fixture", PasswordHash = "x", CreatedAt = now, UpdatedAt = now });
        db.SaveChanges();
        return db;
    }
}
