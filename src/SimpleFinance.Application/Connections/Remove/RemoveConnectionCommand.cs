namespace SimpleFinance.Application.Connections.Remove;

/// <summary>
/// PLG-06: removes a Connection — deletes the Pluggy Item and cascading soft-deletes local data.
/// <see cref="Confirm"/> must be explicitly true (T-02-20) — enforced by
/// <see cref="RemoveConnectionValidator"/>, not just documented, so a missing/false confirmation
/// never reaches the Pluggy DELETE call or any database write.
/// </summary>
public sealed record RemoveConnectionCommand(bool Confirm);
