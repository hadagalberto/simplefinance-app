namespace SimpleFinance.Infrastructure.Ai;

/// <summary>
/// Resolves the current DeepSeek / OpenAI bearer key at REQUEST TIME (not at DI-registration time),
/// so a key edited in Settings → IA takes effect on the very next call without a restart. Resolution
/// order per provider:
/// <list type="number">
///   <item>the single user's <c>UserSettings</c> encrypted column, when non-null → AES-decrypt + Trim;</item>
///   <item>else the <c>Ai:{DeepSeek,OpenAi}ApiKey</c> environment value → Trim;</item>
///   <item>else <c>null</c> (neither configured).</item>
/// </list>
/// Every returned key is TRIMMED — this fixes the production whitespace-paste 401 (a key pasted with a
/// trailing newline into either the env or the UI would otherwise be sent verbatim and rejected).
/// Scoped (reads the DbContext); the singleton bearer handlers resolve it through a fresh scope.
/// </summary>
public interface IAiKeyProvider
{
    /// <summary>The effective DeepSeek key (DB-decrypted, else env), trimmed. Null when neither is set.</summary>
    Task<string?> GetDeepSeekKeyAsync(CancellationToken ct);

    /// <summary>The effective OpenAI key (DB-decrypted, else env), trimmed. Null when neither is set.</summary>
    Task<string?> GetOpenAiKeyAsync(CancellationToken ct);
}
