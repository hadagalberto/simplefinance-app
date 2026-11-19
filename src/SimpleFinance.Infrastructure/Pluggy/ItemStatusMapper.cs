using SimpleFinance.Domain.Enums;

namespace SimpleFinance.Infrastructure.Pluggy;

/// <summary>
/// PLG-04: maps a Connection's two-axis Item state (Status + ExecutionStatus) to a specific
/// suggested action — never a generic "error" bucket (PITFALLS.md #5). Pure, no I/O, directly
/// unit-testable. This is the exact mechanism 02-07's GET /api/connections endpoint calls to
/// surface `suggestedAction` per connection.
/// </summary>
public static class ItemStatusMapper
{
    /// <summary>
    /// The 10 documented transitive (in-progress) ExecutionStatus values that co-occur with
    /// Status=Updating (02-RESEARCH.md's Item State Machine section). Not consulted directly by
    /// <see cref="Map"/> below — Status=Updating always maps to WaitForCompletion regardless of
    /// which specific transitive value accompanies it, since the two axes correlate 1:1 by
    /// Pluggy's own model — kept here purely as living documentation of that correlation.
    /// </summary>
    public static readonly IReadOnlyCollection<string> TransitiveExecutionStatuses =
    [
        "CREATED",
        "LOGIN_IN_PROGRESS",
        "LOGIN_MFA_IN_PROGRESS",
        "ACCOUNTS_IN_PROGRESS",
        "CREDITCARDS_IN_PROGRESS",
        "TRANSACTIONS_IN_PROGRESS",
        "INVESTMENT_TRANSACTIONS_IN_PROGRESS",
        "PAYMENT_DATA_IN_PROGRESS",
        "IDENTITY_IN_PROGRESS",
        "MERGING",
    ];

    private const string PartialSuccessExecutionStatus = "PARTIAL_SUCCESS";
    private const string InvalidCredentialsPrefix = "INVALID_CREDENTIALS";

    public static ConnectionSuggestedAction Map(ConnectionStatus status, string? executionStatus)
    {
        if (status == ConnectionStatus.Updating)
        {
            return ConnectionSuggestedAction.WaitForCompletion;
        }

        if (status == ConnectionStatus.WaitingUserInput)
        {
            return ConnectionSuggestedAction.ProvideMfa;
        }

        if (status == ConnectionStatus.LoginError)
        {
            return ConnectionSuggestedAction.ReconnectRequired;
        }

        // Applies across any status (most relevantly Outdated, per Pluggy's model where a bad
        // reconnect attempt can surface INVALID_CREDENTIALS* under Outdated rather than
        // LoginError) — case-insensitive prefix match, since Pluggy returns upper-snake-case
        // and groups several variants (INVALID_CREDENTIALS, INVALID_CREDENTIALS_MFA, ...) under
        // one reconnect action per 02-RESEARCH.md.
        if (!string.IsNullOrEmpty(executionStatus)
            && executionStatus.StartsWith(InvalidCredentialsPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return ConnectionSuggestedAction.ReconnectRequired;
        }

        if (status == ConnectionStatus.Updated)
        {
            return string.Equals(executionStatus, PartialSuccessExecutionStatus, StringComparison.OrdinalIgnoreCase)
                ? ConnectionSuggestedAction.ReviewPartialFailure
                : ConnectionSuggestedAction.None;
        }

        // Outdated (or any future/unmapped status) with an ExecutionStatus not covered above —
        // the long tail of Pluggy's ~24 executionStatus values. Never throws (02-RESEARCH.md's
        // explicit guidance: new Pluggy error codes must not crash the mapper).
        return ConnectionSuggestedAction.GenericRetry;
    }
}
