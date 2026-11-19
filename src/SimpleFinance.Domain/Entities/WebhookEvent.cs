using SimpleFinance.Domain.Common;
using SimpleFinance.Domain.Enums;

namespace SimpleFinance.Domain.Entities;

/// <summary>
/// Raw persisted intake of a Pluggy webhook call, per PRD §11.2 (no UserId — this is a
/// provider-level event, not scoped to a single user's data yet). Supports SYNC-01's
/// dedup-by-eventId replay-safety requirement.
/// </summary>
public class WebhookEvent : AuditableEntity
{
    /// <summary>Pluggy's <c>eventId</c>. Unique — the dedup backstop for SYNC-01.</summary>
    public string EventId { get; set; } = string.Empty;

    public string EventType { get; set; } = string.Empty;

    public string? PluggyItemId { get; set; }

    /// <summary>
    /// Raw request body, verbatim. PITFALLS.md #12 flags this as a future retention/redaction
    /// policy candidate — a purge/redaction job is explicitly out of Phase 2 scope (Phase 8 ops
    /// concern). Serilog request logging never logs raw bodies, so this is a storage-only, not
    /// log-transit, exposure surface (see threat T-02-01).
    /// </summary>
    public string PayloadJson { get; set; } = string.Empty;

    public DateTime ReceivedAt { get; set; }

    public DateTime? ProcessedAt { get; set; }

    public WebhookEventStatus Status { get; set; }

    public string? Error { get; set; }
}
