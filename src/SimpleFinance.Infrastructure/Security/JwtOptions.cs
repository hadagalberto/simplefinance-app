namespace SimpleFinance.Infrastructure.Security;

/// <summary>
/// Binds to the "Jwt" configuration section, populated via env vars
/// (Jwt__SigningKey, Jwt__AccessTokenMinutes, Jwt__RefreshTokenDays — see .env.example).
/// </summary>
public class JwtOptions
{
    public const string SectionName = "Jwt";

    public string SigningKey { get; set; } = string.Empty;

    public int AccessTokenMinutes { get; set; } = 15;

    public int RefreshTokenDays { get; set; } = 30;
}
