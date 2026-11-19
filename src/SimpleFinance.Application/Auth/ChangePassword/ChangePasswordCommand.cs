namespace SimpleFinance.Application.Auth.ChangePassword;

/// <summary>Request contract for POST /api/auth/change-password (requires a valid access token).</summary>
public sealed record ChangePasswordCommand(string CurrentPassword, string NewPassword);
