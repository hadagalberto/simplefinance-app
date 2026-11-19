using Microsoft.EntityFrameworkCore;
using SimpleFinance.Domain.Entities;
using SimpleFinance.Domain.Enums;
using SimpleFinance.Infrastructure.Persistence;

namespace SimpleFinance.Infrastructure.Ai.Assistant;

/// <summary>
/// A conversation loaded together with its ordered message thread — the shape
/// <see cref="ConversationService.GetWithMessagesAsync"/> returns to the orchestrator (17-04) and the
/// GET-one endpoint (17-05).
/// </summary>
public sealed record ConversationWithMessages(AiConversation Conversation, IReadOnlyList<AiMessage> Messages);

/// <summary>
/// AI-CHAT-01: the UserId-scoped conversation store behind the chat endpoints (17-05) and the
/// orchestrator's turn-persistence (17-04). Provides create / get-with-messages / list (recency) /
/// rename / soft-delete / append-message. Deliberately in Infrastructure (it depends directly on
/// <see cref="SimpleFinanceDbContext"/>), mirroring <see cref="AiSuggestionService"/>'s primary-ctor +
/// load-scoped pattern.
///
/// EVERY query filters <c>WHERE UserId == userId</c> and every mutation loads the row scoped to the
/// caller first, so a foreign or non-existent id simply "isn't found" — null/false, no mutation, no
/// leak (T-17-01/02). The global soft-delete query filter excludes <c>DeletedAt != null</c>
/// automatically.
/// </summary>
public sealed class ConversationService(SimpleFinanceDbContext db)
{
    /// <summary>Create a new empty conversation owned by <paramref name="userId"/> with timestamps set.</summary>
    public async Task<AiConversation> CreateAsync(Guid userId, string title, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var conversation = new AiConversation
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Title = title,
            CreatedAt = now,
            UpdatedAt = now,
        };

        db.AiConversations.Add(conversation);
        await db.SaveChangesAsync(ct);
        return conversation;
    }

    /// <summary>
    /// Load a conversation together with its messages (ordered oldest-first by CreatedAt), scoped to the
    /// caller. Returns null when the id is missing or owned by another user — no leak.
    /// </summary>
    public async Task<ConversationWithMessages?> GetWithMessagesAsync(Guid userId, Guid conversationId, CancellationToken ct)
    {
        var conversation = await db.AiConversations
            .FirstOrDefaultAsync(c => c.Id == conversationId && c.UserId == userId, ct);

        if (conversation is null)
        {
            return null;
        }

        var messages = await db.AiMessages
            .Where(m => m.UserId == userId && m.ConversationId == conversationId)
            .OrderBy(m => m.CreatedAt)
            .ToListAsync(ct);

        return new ConversationWithMessages(conversation, messages);
    }

    /// <summary>
    /// The caller's conversations, most-recent activity first (ordered by UpdatedAt descending),
    /// excluding soft-deleted rows. Backed by the <c>(UserId, UpdatedAt)</c> index.
    /// </summary>
    public async Task<IReadOnlyList<AiConversation>> ListAsync(Guid userId, CancellationToken ct)
    {
        return await db.AiConversations
            .Where(c => c.UserId == userId)
            .OrderByDescending(c => c.UpdatedAt)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Rename a conversation (updates Title + UpdatedAt), scoped to the caller. Returns false when the id
    /// is missing/not owned — no mutation.
    /// </summary>
    public async Task<bool> RenameAsync(Guid userId, Guid id, string title, CancellationToken ct)
    {
        var conversation = await db.AiConversations
            .FirstOrDefaultAsync(c => c.Id == id && c.UserId == userId, ct);

        if (conversation is null)
        {
            return false;
        }

        conversation.Title = title;
        conversation.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// Soft-delete a conversation (stamps DeletedAt), scoped to the caller. Returns false when the id is
    /// missing/not owned — no mutation.
    /// </summary>
    public async Task<bool> DeleteAsync(Guid userId, Guid id, CancellationToken ct)
    {
        var conversation = await db.AiConversations
            .FirstOrDefaultAsync(c => c.Id == id && c.UserId == userId, ct);

        if (conversation is null)
        {
            return false;
        }

        var now = DateTime.UtcNow;
        conversation.DeletedAt = now;
        conversation.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// Append a message turn to a conversation owned by the caller and bump the parent conversation's
    /// UpdatedAt so ListAsync recency ordering reflects activity. Used by the orchestrator to persist
    /// user/assistant/tool turns. Returns null when the conversation is missing/not owned — no insert,
    /// no leak.
    /// </summary>
    public async Task<AiMessage?> AppendMessageAsync(
        Guid userId,
        Guid conversationId,
        MessageRole role,
        string? content,
        string? toolName = null,
        string? toolCallsJson = null,
        string? toolResultJson = null,
        string? model = null,
        int? promptTokens = null,
        int? completionTokens = null,
        CancellationToken ct = default)
    {
        var conversation = await db.AiConversations
            .FirstOrDefaultAsync(c => c.Id == conversationId && c.UserId == userId, ct);

        if (conversation is null)
        {
            return null;
        }

        var now = DateTime.UtcNow;
        var message = new AiMessage
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            ConversationId = conversationId,
            Role = role,
            Content = content,
            ToolName = toolName,
            ToolCallsJson = toolCallsJson,
            ToolResultJson = toolResultJson,
            Model = model,
            PromptTokens = promptTokens,
            CompletionTokens = completionTokens,
            CreatedAt = now,
            UpdatedAt = now,
        };

        db.AiMessages.Add(message);
        conversation.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        return message;
    }
}
