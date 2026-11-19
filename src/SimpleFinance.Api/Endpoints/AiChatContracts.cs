using FluentValidation;

namespace SimpleFinance.Api.Endpoints;

/// <summary>
/// AI-CHAT-01: request/response contracts for the streaming chat endpoint + conversation CRUD
/// (<c>POST /api/ai/chat</c>, <c>GET/PATCH/DELETE /api/ai/conversations</c>). Strongly-typed records
/// mirroring the <c>AiConversation</c>/<c>AiMessage</c> entities — no loosely-typed dictionaries at the
/// boundary. Live in the Api assembly (not Application) because the endpoints wrap Infrastructure
/// services directly and the validators here have no cross-assembly command to attach to; they are
/// registered explicitly in Program.cs (AddValidatorsFromAssemblyContaining scans Application only).
/// </summary>
public sealed record ChatRequest(Guid? ConversationId, string Message);

/// <summary>PATCH /api/ai/conversations/{id} body — the new conversation title.</summary>
public sealed record RenameConversationRequest(string Title);

/// <summary>GET /api/ai/conversations row — one conversation's metadata, newest-activity first.</summary>
public sealed record ConversationSummary(Guid Id, string Title, DateTime CreatedAt, DateTime UpdatedAt);

/// <summary>
/// One persisted turn in a conversation thread (GET /api/ai/conversations/{id}). <see cref="Role"/> is
/// the neutral role name (User/Assistant/Tool); <see cref="ToolName"/> is set only on tool-result turns.
/// </summary>
public sealed record ChatMessageDto(
    Guid Id, string Role, string? Content, string? ToolName, DateTime CreatedAt);

/// <summary>GET /api/ai/conversations/{id} response — the conversation plus its ordered message thread.</summary>
public sealed record ConversationDetail(
    Guid Id, string Title, DateTime CreatedAt, DateTime UpdatedAt, IReadOnlyList<ChatMessageDto> Messages);

/// <summary>
/// AI-CHAT-01 (V5 input validation): the chat message must be present and bounded. A blank or oversized
/// message is a 400 before the orchestrator (and any LLM spend) is ever reached.
/// </summary>
public sealed class ChatRequestValidator : AbstractValidator<ChatRequest>
{
    public const int MaxMessageLength = 4000;

    public ChatRequestValidator()
    {
        RuleFor(r => r.Message)
            .NotEmpty()
            .MaximumLength(MaxMessageLength);
    }
}

/// <summary>
/// AI-CHAT-01 (V5 input validation): a rename must supply a non-empty title within the entity's 200-char
/// column bound.
/// </summary>
public sealed class RenameConversationRequestValidator : AbstractValidator<RenameConversationRequest>
{
    public const int MaxTitleLength = 200;

    public RenameConversationRequestValidator()
    {
        RuleFor(r => r.Title)
            .NotEmpty()
            .MaximumLength(MaxTitleLength);
    }
}
