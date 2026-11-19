namespace SimpleFinance.Application.Ai.Assistant;

/// <summary>
/// AI-CHAT-02: the registry of tools advertised to the model. Built by DI from every registered
/// <see cref="IAssistantTool"/> (read tools now; the RAG tool in 17-03 and the write tools in 17-06
/// register into this same catalog with zero changes here). <see cref="AsLlmTools"/> projects the tool
/// schemas onto <c>LlmChatRequest.Tools</c>; <see cref="Tools"/> backs the <c>ToolDispatcher</c>'s
/// name→handler map. Pure aggregation — no Infrastructure dependency, so it can live in Application.
/// </summary>
public sealed class ToolCatalog
{
    private readonly IReadOnlyList<IAssistantTool> _tools;

    public ToolCatalog(IEnumerable<IAssistantTool> tools)
    {
        _tools = tools.ToList();
    }

    /// <summary>Every registered tool (read + RAG + write, as later waves add them).</summary>
    public IReadOnlyList<IAssistantTool> Tools => _tools;

    /// <summary>
    /// The tool JSON schemas to advertise to the model on <c>LlmChatRequest.Tools</c>. The orchestrator
    /// (17-04) passes this straight into <see cref="ILlmClient.ChatAsync"/>.
    /// </summary>
    public IReadOnlyList<LlmTool> AsLlmTools() => _tools.Select(t => t.Schema).ToList();
}
