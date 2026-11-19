using SimpleFinance.Domain.Common;
using SimpleFinance.Domain.Enums;

namespace SimpleFinance.Domain.Entities;

/// <summary>
/// One turn in an <see cref="AiConversation"/> (AI-CHAT-01). A <see cref="MessageRole.User"/> turn
/// carries the question; an <see cref="MessageRole.Assistant"/> turn the answer (with optional
/// tool-call requests in <see cref="ToolCallsJson"/> and token/model accounting); a
/// <see cref="MessageRole.Tool"/> turn the result of a tool the assistant invoked
/// (<see cref="ToolName"/> + <see cref="ToolResultJson"/>). Token/model columns mirror
/// <see cref="AiUsageLog"/>. Per-user (inherits <see cref="UserOwnedEntity"/>); ordered within a
/// thread by <see cref="UserOwnedEntity.CreatedAt"/>.
/// </summary>
public class AiMessage : UserOwnedEntity
{
    /// <summary>The thread this message belongs to.</summary>
    public Guid ConversationId { get; set; }

    /// <summary>Who authored the turn (User/Assistant/Tool).</summary>
    public MessageRole Role { get; set; }

    /// <summary>The natural-language content of the turn. Null for a pure tool-call assistant turn.</summary>
    public string? Content { get; set; }

    /// <summary>For a Tool turn: the name of the tool that produced the result.</summary>
    public string? ToolName { get; set; }

    /// <summary>For an Assistant turn: the tool-call requests as a jsonb document.</summary>
    public string? ToolCallsJson { get; set; }

    /// <summary>For a Tool turn: the tool's result as a jsonb document.</summary>
    public string? ToolResultJson { get; set; }

    /// <summary>The model that produced an Assistant turn (config-driven, e.g. deepseek-chat).</summary>
    public string? Model { get; set; }

    /// <summary>Prompt-token count for an Assistant turn (cost accounting), null otherwise.</summary>
    public int? PromptTokens { get; set; }

    /// <summary>Completion-token count for an Assistant turn (cost accounting), null otherwise.</summary>
    public int? CompletionTokens { get; set; }
}
