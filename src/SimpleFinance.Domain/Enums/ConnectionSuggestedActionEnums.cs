namespace SimpleFinance.Domain.Enums;

/// <summary>
/// The user-facing action a <see cref="SimpleFinance.Domain.Entities.Connection"/>'s current
/// two-axis Item state (Status + ExecutionStatus) suggests. Computed by Infrastructure's
/// ItemStatusMapper (02-02) — never a raw pass-through of Pluggy's status strings, per
/// PITFALLS.md #5's "never a generic error bucket" warning. Placed in Domain.Enums (not
/// Infrastructure.Pluggy) so 02-07's GET /api/connections endpoint can serialize it directly
/// without an Infrastructure dependency leaking into the Api layer.
/// </summary>
public enum ConnectionSuggestedAction
{
    /// <summary>Updated + Success — nothing to show, connection is healthy.</summary>
    None,

    /// <summary>Updating (any transitive ExecutionStatus) — show progress, don't prompt the user.</summary>
    WaitForCompletion,

    /// <summary>WaitingUserInput — a real MFA challenge; reopen the widget in update mode.</summary>
    ProvideMfa,

    /// <summary>LoginError, or any INVALID_CREDENTIALS* ExecutionStatus — reopen widget in update mode.</summary>
    ReconnectRequired,

    /// <summary>Updated + PartialSuccess — surface which product failed (StatusDetail), not a generic error.</summary>
    ReviewPartialFailure,

    /// <summary>Outdated with an unmapped/unknown ExecutionStatus — long-tail fallback, never throws.</summary>
    GenericRetry
}
