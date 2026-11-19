namespace SimpleFinance.Application.Auth.Setup;

/// <summary>
/// First-run "create the single user" command. AUTH-01: the /api/auth/setup endpoint accepts
/// this exactly once — a second call is rejected with 409 once any User row exists.
/// </summary>
public sealed record SetupUserCommand(string Name, string Email, string Password);
