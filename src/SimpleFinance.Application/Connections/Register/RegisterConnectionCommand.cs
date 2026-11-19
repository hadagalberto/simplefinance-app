namespace SimpleFinance.Application.Connections.Register;

/// <summary>
/// PLG-03: persists a Connection from the Pluggy Connect Widget's <c>onSuccess(itemId)</c>
/// callback and triggers the first sync. Idempotent-by-itemId — a duplicate PluggyItemId
/// returns 409 rather than a second row (T-02-21).
/// </summary>
public sealed record RegisterConnectionCommand(string PluggyItemId);
