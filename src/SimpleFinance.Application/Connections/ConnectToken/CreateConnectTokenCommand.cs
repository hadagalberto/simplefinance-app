namespace SimpleFinance.Application.Connections.ConnectToken;

/// <summary>
/// PLG-02/PLG-05: requests a fresh Connect Token from the provider. Leave
/// <see cref="ExistingPluggyItemId"/> null for a brand-new connection (Task 1's create path);
/// populate it to open the widget in update/reconnect mode for an existing Item (Task 2's
/// POST /api/connections/{id}/reconnect-token route reuses this exact command shape).
/// </summary>
public sealed record CreateConnectTokenCommand(string? ExistingPluggyItemId);
