using System.Text.Json;
using SimpleFinance.Application.Ai;
using SimpleFinance.Application.Ai.Assistant;
using SimpleFinance.Infrastructure.Ai.Assistant;

namespace SimpleFinance.UnitTests.Assistant;

/// <summary>
/// Behavior tests for <see cref="ToolCatalog"/> + <see cref="ToolDispatcher"/> (17-02, AI-CHAT-02).
/// Asserts the name→handler routing, the userId pass-through, that <see cref="ToolCatalog.AsLlmTools"/>
/// advertises each registered tool's schema, and — the T-17-03 invariant — that a tool name the model
/// invents that is not registered returns a <c>{ "error": "unknown_tool" }</c> result instead of
/// throwing the loop.
/// </summary>
public class ToolDispatcherTests
{
    [Fact]
    public async Task ExecuteAsync_RoutesToNamedTool_PassesUserId()
    {
        var userId = Guid.NewGuid();
        var tool = new RecordingTool("sum_by_category");
        var dispatcher = new ToolDispatcher(new ToolCatalog([tool]));

        var result = await dispatcher.ExecuteAsync(
            new LlmToolCall("call-1", "sum_by_category", """{"period":"2026-05"}"""), userId, CancellationToken.None);

        Assert.Equal("""{"ok":true}""", result);
        Assert.Equal(userId, tool.LastUserId);
        Assert.Equal("""{"period":"2026-05"}""", tool.LastArgs);
    }

    [Fact]
    public async Task ExecuteAsync_UnknownTool_ReturnsErrorResult_DoesNotThrow()
    {
        var dispatcher = new ToolDispatcher(new ToolCatalog([new RecordingTool("known_tool")]));

        var result = await dispatcher.ExecuteAsync(
            new LlmToolCall("call-2", "totally_made_up", "{}"), Guid.NewGuid(), CancellationToken.None);

        using var doc = JsonDocument.Parse(result);
        Assert.Equal("unknown_tool", doc.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task ExecuteAsync_ToolThrows_ReturnsErrorResult_NeverBreaksLoop()
    {
        var dispatcher = new ToolDispatcher(new ToolCatalog([new ThrowingTool("boom")]));

        var result = await dispatcher.ExecuteAsync(
            new LlmToolCall("call-3", "boom", "{}"), Guid.NewGuid(), CancellationToken.None);

        using var doc = JsonDocument.Parse(result);
        Assert.Equal("tool_failed", doc.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public void AsLlmTools_ExposesEverySchema()
    {
        var catalog = new ToolCatalog([new RecordingTool("a"), new RecordingTool("b")]);

        var tools = catalog.AsLlmTools();

        Assert.Equal(2, tools.Count);
        Assert.Contains(tools, t => t.Name == "a");
        Assert.Contains(tools, t => t.Name == "b");
    }

    private sealed class RecordingTool(string name) : IAssistantTool
    {
        public string Name => name;
        public bool IsWrite => false;
        public LlmTool Schema => new(name, "test tool", ToolSchema.Parse("""{"type":"object"}"""));
        public Guid LastUserId { get; private set; }
        public string? LastArgs { get; private set; }

        public Task<string> ExecuteAsync(string argsJson, Guid userId, CancellationToken ct)
        {
            LastUserId = userId;
            LastArgs = argsJson;
            return Task.FromResult("""{"ok":true}""");
        }
    }

    private sealed class ThrowingTool(string name) : IAssistantTool
    {
        public string Name => name;
        public bool IsWrite => false;
        public LlmTool Schema => new(name, null, ToolSchema.Parse("""{"type":"object"}"""));

        public Task<string> ExecuteAsync(string argsJson, Guid userId, CancellationToken ct) =>
            throw new InvalidOperationException("simulated tool failure");
    }
}
