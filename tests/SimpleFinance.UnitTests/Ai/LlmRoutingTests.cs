using System.Runtime.CompilerServices;
using SimpleFinance.Application.Ai;
using SimpleFinance.Domain.Enums;
using SimpleFinance.Infrastructure.Ai.Routing;

namespace SimpleFinance.UnitTests.Ai;

/// <summary>
/// Model-name → provider routing: the pure <see cref="LlmProviderResolver"/> rule and the
/// <see cref="RoutingLlmClient"/> that delegates each call to the client the rule picks.
/// </summary>
public class LlmRoutingTests
{
    [Theory]
    [InlineData("deepseek-chat", AiProvider.DeepSeek)]
    [InlineData("deepseek-reasoner", AiProvider.DeepSeek)]
    [InlineData("deepseek-v4-flash", AiProvider.DeepSeek)]
    [InlineData("claude-sonnet-5-5", AiProvider.Anthropic)]
    [InlineData("claude-opus-5-5", AiProvider.Anthropic)]
    [InlineData("Claude-Haiku-5-5", AiProvider.Anthropic)]
    [InlineData("  claude-haiku-5-5  ", AiProvider.Anthropic)]
    [InlineData("openai/gpt-5", AiProvider.OpenRouter)]
    [InlineData("anthropic/claude-sonnet-5-5", AiProvider.OpenRouter)]
    [InlineData("meta-llama/llama-4-maverick:free", AiProvider.OpenRouter)]
    [InlineData("", AiProvider.DeepSeek)]
    [InlineData(null, AiProvider.DeepSeek)]
    [InlineData("claude", AiProvider.DeepSeek)]
    public void Resolver_MapsModelNameToProvider(string? model, AiProvider expected)
    {
        Assert.Equal(expected, LlmProviderResolver.Resolve(model));
    }

    private sealed class NamedFakeClient(string name) : ILlmClient
    {
        public List<string> Calls { get; } = [];

        public Task<LlmResult> ChatAsync(LlmChatRequest request, CancellationToken ct)
        {
            Calls.Add(request.Model);
            return Task.FromResult(new LlmResult(name, null, [], new TokenUsage(1, 1)));
        }

        public async IAsyncEnumerable<LlmStreamChunk> ChatStreamAsync(LlmChatRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            Calls.Add(request.Model);
            await Task.Yield();
            yield return new LlmStreamChunk(ContentDelta: name);
        }
    }

    private static LlmChatRequest Request(string model) =>
        new(model, [new LlmMessage("user", "oi")], AiFeature.Assistant);

    [Fact]
    public async Task RoutingClient_DelegatesEachCallToTheResolvedProvider()
    {
        var deepSeek = new NamedFakeClient("deepseek");
        var anthropic = new NamedFakeClient("anthropic");
        var openRouter = new NamedFakeClient("openrouter");
        var router = new RoutingLlmClient(p => p switch
        {
            AiProvider.Anthropic => anthropic,
            AiProvider.OpenRouter => openRouter,
            _ => deepSeek,
        });

        Assert.Equal("deepseek", (await router.ChatAsync(Request("deepseek-chat"), CancellationToken.None)).Content);
        Assert.Equal("anthropic", (await router.ChatAsync(Request("claude-sonnet-5-5"), CancellationToken.None)).Content);
        Assert.Equal("openrouter", (await router.ChatAsync(Request("openai/gpt-5"), CancellationToken.None)).Content);

        var streamed = new List<string?>();
        await foreach (var chunk in router.ChatStreamAsync(Request("claude-opus-5-5"), CancellationToken.None))
        {
            streamed.Add(chunk.ContentDelta);
        }

        Assert.Equal(["anthropic"], streamed);
        Assert.Equal(["deepseek-chat"], deepSeek.Calls);
        Assert.Equal(["claude-sonnet-5-5", "claude-opus-5-5"], anthropic.Calls);
        Assert.Equal(["openai/gpt-5"], openRouter.Calls);
    }

    [Fact]
    public async Task RoutingClient_OnlyResolvesTheProviderItUses()
    {
        var built = new List<string>();
        var router = new RoutingLlmClient(provider =>
        {
            built.Add(provider.ToString());
            return new NamedFakeClient(provider.ToString());
        });

        await router.ChatAsync(Request("claude-haiku-5-5"), CancellationToken.None);

        Assert.Equal(["Anthropic"], built);
    }
}
