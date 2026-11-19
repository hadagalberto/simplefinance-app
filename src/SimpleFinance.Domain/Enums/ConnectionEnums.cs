namespace SimpleFinance.Domain.Enums;

/// <summary>
/// Top-level Pluggy Item <c>status</c> — exactly the 5 values verified in
/// 02-RESEARCH.md's Item State Machine section. Do NOT add a 6th "Error"/"MergeError" value:
/// per RESEARCH.md, <c>MERGE_ERROR</c> and the rest of Pluggy's ~24 error codes are
/// <c>executionStatus</c> values, not <c>status</c> values — they are represented by
/// <see cref="SimpleFinance.Domain.Entities.Connection.ExecutionStatus"/> (free-text string),
/// not this enum. This corrects PRD §11.2's stale 6-value assumption per the mandatory
/// RESEARCH.md override rule.
/// </summary>
public enum ConnectionStatus
{
    Updating,
    Updated,
    LoginError,
    WaitingUserInput,
    Outdated
}
