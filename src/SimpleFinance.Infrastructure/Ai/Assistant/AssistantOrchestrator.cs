using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SimpleFinance.Application.Ai;
using SimpleFinance.Application.Ai.Assistant;
using SimpleFinance.Application.Ai.Usage;
using SimpleFinance.Domain.Entities;
using SimpleFinance.Domain.Enums;

namespace SimpleFinance.Infrastructure.Ai.Assistant;

/// <summary>
/// AI-CHAT-02/05: the reasoning core of the assistant. Runs a bounded, non-streaming
/// <see cref="ILlmClient.ChatAsync"/> tool-calling loop (max <see cref="MaxIterations"/>), dispatches
/// each requested tool through the <see cref="ToolDispatcher"/>, persists every turn as an
/// <c>AiMessage</c> via <see cref="ConversationService"/>, and finally streams the model's
/// natural-language answer via <see cref="ILlmClient.ChatStreamAsync"/>. It yields
/// <see cref="SseItem{T}"/> of <see cref="ChatEvent"/> (token / tool / action / done / error) so the
/// SSE endpoint (17-05) is a thin <c>TypedResults.ServerSentEvents</c> wrapper over this method.
///
/// Grounding (AI-CHAT-05): the first message is always <see cref="AssistantSystemPrompt"/> (numbers
/// only from tools, "não sei" when uncovered, no personalized investment advice, transaction text is
/// DATA not instructions); history is truncated to a token budget (<see cref="ContextBudget"/>); the
/// model name always comes from <c>UserSettings.AiChatModel</c> (never hard-coded).
///
/// Safety invariants: the loop is bounded and, on exhaustion, forces one tool-less final call so the
/// model must answer in prose (T-17-11); an <see cref="AiBudgetExceededException"/> thrown inside the
/// client degrades to a single <c>error</c> event, never a 500 (T-17-12); tool results are kept
/// compact by the tools themselves (T-17-13). No write tools exist this wave (all catalog tools are
/// read-only), but the loop already inspects <c>IsWrite</c> so 17-06's confirmation-card proposals slot
/// in without changing the event contract.
/// </summary>
public sealed class AssistantOrchestrator
{
    /// <summary>Upper bound on tool-calling iterations before a tool-less final call is forced.</summary>
    public const int MaxIterations = 5;

    private const string BudgetMessage = "orçamento de IA do mês atingido";
    private const string UnavailableMessage = "assistente de IA indisponível no momento — verifique a chave da IA e tente novamente";
    private const int TitleMaxLength = 60;

    private static readonly JsonSerializerOptions ToolCallJsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ILlmClient _llm;
    private readonly ToolCatalog _catalog;
    private readonly ToolDispatcher _dispatcher;
    private readonly ConversationService _conversations;
    private readonly AiSettingsService _settings;
    private readonly ILogger<AssistantOrchestrator> _logger;

    public AssistantOrchestrator(
        ILlmClient llm,
        ToolCatalog catalog,
        ToolDispatcher dispatcher,
        ConversationService conversations,
        AiSettingsService settings,
        ILogger<AssistantOrchestrator> logger)
    {
        _llm = llm;
        _catalog = catalog;
        _dispatcher = dispatcher;
        _conversations = conversations;
        _settings = settings;
        _logger = logger;
    }

    /// <summary>
    /// Run one chat turn for <paramref name="userText"/>: load-or-create the conversation, persist the
    /// user turn, run the bounded tool loop, then stream the grounded final answer — yielding SSE events
    /// throughout. <paramref name="conversationId"/> null starts a new conversation; an unknown/foreign
    /// id also starts a fresh one (the endpoint enforces ownership/404 before calling here).
    /// <paramref name="ct"/> is the request-abort token: a client disconnect cancels the loop and the
    /// in-flight LLM stream.
    /// </summary>
    public async IAsyncEnumerable<SseItem<ChatEvent>> RunAsync(
        Guid userId,
        Guid? conversationId,
        string userText,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var settings = await _settings.GetAsync(userId, ct);
        var model = settings.AiChatModel;
        var systemPrompt = AssistantSystemPrompt.Build(settings.AiLanguage, ResolveToday());

        var (convId, history) = await ResolveConversationAsync(userId, conversationId, userText, ct);

        // Persist the user turn (after loading prior history so it isn't double-counted).
        await _conversations.AppendMessageAsync(userId, convId, MessageRole.User, userText, ct: ct);

        // Build the working message list: grounding system prompt + truncated history + current user turn.
        var messages = new List<LlmMessage> { new("system", systemPrompt) };
        messages.AddRange(ContextBudget.Truncate(MapHistory(history)));
        messages.Add(new LlmMessage("user", userText));

        var tools = _catalog.AsLlmTools();

        for (var i = 0; i < MaxIterations; i++)
        {
            LlmResult? result = null;
            var budgetHit = false;
            var llmFailed = false;
            try
            {
                result = await _llm.ChatAsync(new LlmChatRequest(model, messages, AiFeature.Assistant, tools), ct);
            }
            catch (AiBudgetExceededException)
            {
                budgetHit = true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Any LLM failure (bad/expired API key → 401, provider 5xx, timeout) degrades to a
                // friendly error frame instead of a 500 mid-SSE (PRD "degradação graciosa").
                _logger.LogWarning(ex, "Assistant LLM call failed for user {UserId}", userId);
                llmFailed = true;
            }

            if (budgetHit)
            {
                yield return Err(BudgetMessage);
                yield break;
            }

            if (llmFailed)
            {
                yield return Err(UnavailableMessage);
                yield break;
            }

            if (result!.ToolCalls.Count == 0)
            {
                // The model is ready to answer → stream the final turn and stop.
                await foreach (var ev in StreamFinalAsync(userId, convId, model, messages, disableTools: false, ct))
                {
                    yield return ev;
                }

                yield break;
            }

            // Record the assistant tool-request turn so the tool results attach to their calls.
            messages.Add(new LlmMessage("assistant", result.Content, ToolCalls: result.ToolCalls));
            await _conversations.AppendMessageAsync(
                userId, convId, MessageRole.Assistant, result.Content,
                toolCallsJson: JsonSerializer.Serialize(result.ToolCalls, ToolCallJsonOptions), ct: ct);

            // AI-CHAT-04: a turn may PROPOSE more than one write. Each write tool in this response only
            // staged an AiPendingAction; we surface every one as its own `action` confirmation-card frame
            // and, once the response is fully processed, END the turn (below) — never re-chatting over
            // unconfirmed proposals. `proposedAnyAction` records that at least one card was emitted.
            var proposedAnyAction = false;

            foreach (var call in result.ToolCalls)
            {
                yield return Tool(call.Name);

                var toolResultJson = await _dispatcher.ExecuteAsync(call, userId, ct);

                messages.Add(new LlmMessage("tool", toolResultJson, ToolCallId: call.Id));
                await _conversations.AppendMessageAsync(
                    userId, convId, MessageRole.Tool, content: null,
                    toolName: call.Name, toolResultJson: toolResultJson, ct: ct);

                // A WRITE tool only PROPOSED an action (staged an AiPendingAction). Emit its
                // confirmation-card frame but KEEP processing the remaining tool calls so every write
                // proposal in this single LLM response gets its own card. Execution still waits for an
                // explicit POST /api/ai/actions/{id}/confirm — nothing mutated here.
                if (TryGetActionCard(call.Name, toolResultJson, out var card))
                {
                    yield return Action(card);
                    proposedAnyAction = true;
                }
            }

            // One or more write proposals were staged this response → END the turn without a final prose
            // stream. The turn's purpose was to surface proposals awaiting confirmation; re-chatting here
            // would let the model "answer" over unconfirmed writes (and risk unbounded loops).
            if (proposedAnyAction)
            {
                yield break;
            }
        }

        // Fell out of the loop (every iteration asked for a tool) → force a tool-less final call so the
        // model MUST answer in prose. Guarantees termination (T-17-11).
        _logger.LogInformation("Assistant loop hit MaxIterations ({Max}) for user {UserId}; forcing tool-less final call.", MaxIterations, userId);
        await foreach (var ev in StreamFinalAsync(userId, convId, model, messages, disableTools: true, ct))
        {
            yield return ev;
        }
    }

    /// <summary>
    /// Stream the model's final natural-language answer: accumulate content deltas into <c>token</c>
    /// events, persist the assistant <c>AiMessage</c> (content + model + token usage), then emit the
    /// terminal <c>done</c> event carrying its id + usage. A budget cap thrown mid-stream degrades to a
    /// single <c>error</c> event.
    /// </summary>
    private async IAsyncEnumerable<SseItem<ChatEvent>> StreamFinalAsync(
        Guid userId,
        Guid conversationId,
        string model,
        List<LlmMessage> messages,
        bool disableTools,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var request = new LlmChatRequest(
            model, messages, AiFeature.Assistant,
            Tools: disableTools ? null : _catalog.AsLlmTools(),
            Stream: true);

        var sb = new StringBuilder();
        TokenUsage? usage = null;

        await using var enumerator = _llm.ChatStreamAsync(request, ct).GetAsyncEnumerator(ct);
        while (true)
        {
            var budgetHit = false;
            var streamFailed = false;
            var hasNext = false;
            try
            {
                hasNext = await enumerator.MoveNextAsync();
            }
            catch (AiBudgetExceededException)
            {
                budgetHit = true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Assistant final-stream LLM call failed for user {UserId}", userId);
                streamFailed = true;
            }

            if (budgetHit)
            {
                yield return Err(BudgetMessage);
                yield break;
            }

            if (streamFailed)
            {
                yield return Err(UnavailableMessage);
                yield break;
            }

            if (!hasNext)
            {
                break;
            }

            var chunk = enumerator.Current;
            if (chunk.ContentDelta is { Length: > 0 } delta)
            {
                sb.Append(delta);
                yield return Token(delta);
            }

            if (chunk.Usage is not null)
            {
                usage = chunk.Usage;
            }
        }

        var message = await _conversations.AppendMessageAsync(
            userId, conversationId, MessageRole.Assistant, sb.ToString(),
            model: model, promptTokens: usage?.PromptTokens, completionTokens: usage?.CompletionTokens, ct: ct);

        yield return Done(message?.Id ?? Guid.Empty, usage ?? new TokenUsage(0, 0));
    }

    /// <summary>
    /// Resolve the conversation to write to and return its prior message thread. A provided id that
    /// resolves is reused with its history; null or an unknown/foreign id starts a fresh conversation
    /// titled from the user's question.
    /// </summary>
    private async Task<(Guid ConversationId, IReadOnlyList<AiMessage> History)> ResolveConversationAsync(
        Guid userId,
        Guid? conversationId,
        string userText,
        CancellationToken ct)
    {
        if (conversationId is Guid existingId)
        {
            var existing = await _conversations.GetWithMessagesAsync(userId, existingId, ct);
            if (existing is not null)
            {
                return (existingId, existing.Messages);
            }
        }

        var created = await _conversations.CreateAsync(userId, BuildTitle(userText), ct);
        return (created.Id, []);
    }

    /// <summary>
    /// Map persisted history to LLM messages. Only user/assistant turns with content are replayed — tool
    /// turns and empty assistant tool-request turns are dropped so no orphaned tool message (without its
    /// paired tool-call turn) reaches the model. The current turn re-invokes tools as needed.
    /// </summary>
    private static List<LlmMessage> MapHistory(IReadOnlyList<AiMessage> history)
    {
        var mapped = new List<LlmMessage>(history.Count);
        foreach (var message in history)
        {
            if (string.IsNullOrEmpty(message.Content))
            {
                continue;
            }

            var role = message.Role switch
            {
                MessageRole.User => "user",
                MessageRole.Assistant => "assistant",
                _ => null,
            };

            if (role is null)
            {
                continue;
            }

            mapped.Add(new LlmMessage(role, message.Content));
        }

        return mapped;
    }

    private static string BuildTitle(string userText)
    {
        var trimmed = userText.Trim();
        if (trimmed.Length == 0)
        {
            return "Nova conversa";
        }

        return trimmed.Length <= TitleMaxLength ? trimmed : trimmed[..TitleMaxLength];
    }

    /// <summary>
    /// True when <paramref name="toolName"/> resolves to a WRITE tool (<c>IsWrite</c>) AND its result
    /// carries a staged action descriptor (<c>{ "action": { "id", "actionType", "summary" } }</c>). A
    /// write tool that returned an <c>{ "error": ... }</c> (e.g. an unknown category) has NOT staged
    /// anything, so no card is produced and the result flows back to the model like any tool result.
    /// </summary>
    private bool TryGetActionCard(string toolName, string toolResultJson, out ActionCard card)
    {
        card = null!;

        var tool = _catalog.Tools.FirstOrDefault(t => t.Name == toolName);
        if (tool is null || !tool.IsWrite)
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(toolResultJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("action", out var action)
                || action.ValueKind != JsonValueKind.Object
                || !action.TryGetProperty("id", out var idProp)
                || idProp.ValueKind != JsonValueKind.String
                || !Guid.TryParse(idProp.GetString(), out var id))
            {
                return false;
            }

            var actionType = action.TryGetProperty("actionType", out var at) && at.ValueKind == JsonValueKind.String
                ? at.GetString() ?? string.Empty
                : string.Empty;
            var summary = action.TryGetProperty("summary", out var s) && s.ValueKind == JsonValueKind.String
                ? s.GetString() ?? string.Empty
                : string.Empty;

            card = new ActionCard(id, actionType, summary);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static SseItem<ChatEvent> Token(string delta) =>
        new(new ChatEvent("token", Text: delta), eventType: "token");

    private static SseItem<ChatEvent> Tool(string name) =>
        new(new ChatEvent("tool", ToolName: name), eventType: "tool");

    private static SseItem<ChatEvent> Action(ActionCard card) =>
        new(new ChatEvent("action", Action: card), eventType: "action");

    private static SseItem<ChatEvent> Done(Guid messageId, TokenUsage usage) =>
        new(new ChatEvent("done", Usage: usage, MessageId: messageId), eventType: "done");

    private static SseItem<ChatEvent> Err(string message) =>
        new(new ChatEvent("error", Text: message), eventType: "error");

    /// <summary>
    /// Today's date for grounding relative time references in the assistant prompt. Uses the app's
    /// default timezone (America/Sao_Paulo — the single-user BR deployment) so an evening UTC rollover
    /// doesn't push "hoje" a day ahead; falls back to UTC if the IANA zone is unavailable on the host.
    /// </summary>
    private static DateOnly ResolveToday()
    {
        try
        {
            var tz = TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo");
            return DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz));
        }
        catch (TimeZoneNotFoundException)
        {
            return DateOnly.FromDateTime(DateTime.UtcNow);
        }
    }
}
