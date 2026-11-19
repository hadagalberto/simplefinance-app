using SimpleFinance.Domain.Common;

namespace SimpleFinance.Domain.Entities;

/// <summary>
/// A chat thread between the user and the financial assistant (AI-CHAT-01). Holds only the thread's
/// metadata — its turns live in <see cref="AiMessage"/> rows keyed by <c>ConversationId</c>. The
/// <see cref="UserOwnedEntity.UpdatedAt"/> timestamp is bumped whenever a message is appended so the
/// conversation list can be ordered by most-recent activity. Per-user (inherits
/// <see cref="UserOwnedEntity"/> for UserId/Id/CreatedAt/UpdatedAt/DeletedAt); soft-deleted on delete.
/// </summary>
public class AiConversation : UserOwnedEntity
{
    /// <summary>Short human-readable thread title (user-editable via rename).</summary>
    public string Title { get; set; } = string.Empty;
}
