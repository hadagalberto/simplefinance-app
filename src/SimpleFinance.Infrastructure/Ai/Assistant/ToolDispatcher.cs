using System.Text.Json;
using SimpleFinance.Application.Ai;
using SimpleFinance.Application.Ai.Assistant;

namespace SimpleFinance.Infrastructure.Ai.Assistant;

/// <summary>
/// AI-CHAT-02: the name→handler registry the orchestrator (17-04) calls for every model tool call.
/// Built from the <see cref="ToolCatalog"/> (so read tools now, RAG in 17-03 and write tools in 17-06
/// all route through the same dispatcher). Resolves the tool by <see cref="LlmToolCall.Name"/> and
/// returns its JSON result; a name the model invents that is not registered returns
/// <c>{ "error": "unknown_tool" }</c> instead of throwing, so the tool loop never breaks (T-17-03).
/// The tool itself validates its typed args and runs a fixed parameterized query scoped to
/// <paramref name="userId"/> — the dispatcher never composes SQL and never sees the wrapped services.
/// </summary>
public sealed class ToolDispatcher
{
    private const string UnknownToolResult = """{"error":"unknown_tool"}""";
    private const string ToolFailedResult = """{"error":"tool_failed"}""";

    private readonly IReadOnlyDictionary<string, IAssistantTool> _tools;

    public ToolDispatcher(ToolCatalog catalog)
    {
        _tools = catalog.Tools.ToDictionary(t => t.Name, StringComparer.Ordinal);
    }

    /// <summary>
    /// Run the tool named by <paramref name="call"/> for <paramref name="userId"/>. Unknown name →
    /// <c>{ "error": "unknown_tool" }</c>. Any other tool failure (bad args, transient data error) →
    /// <c>{ "error": "tool_failed" }</c> — never a thrown exception that would abort the loop.
    /// Cancellation is always propagated (never swallowed as a tool failure).
    /// </summary>
    public async Task<string> ExecuteAsync(LlmToolCall call, Guid userId, CancellationToken ct)
    {
        if (!_tools.TryGetValue(call.Name, out var tool))
        {
            return UnknownToolResult;
        }

        try
        {
            return await tool.ExecuteAsync(call.ArgumentsJson, userId, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (JsonException)
        {
            return """{"error":"invalid_arguments"}""";
        }
        catch (Exception)
        {
            return ToolFailedResult;
        }
    }

    /// <summary>True when a tool with this name is registered — the orchestrator can pre-check.</summary>
    public bool IsKnown(string name) => _tools.ContainsKey(name);
}
