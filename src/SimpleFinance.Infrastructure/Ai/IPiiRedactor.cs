namespace SimpleFinance.Infrastructure.Ai;

/// <summary>
/// AI-FND-06: the mandatory PII scrub every AI provider client (15-05) runs on every outbound
/// string BEFORE it leaves the VPS. Callers depend on <see cref="Application.Ai.ILlmClient"/> /
/// <c>IEmbeddingClient</c> only; the concrete clients invoke this internally so the redaction is
/// un-bypassable (T-15-04). Masks CPF/CNPJ, Luhn-valid card PANs, long account/agency runs, and
/// token/UUID strings that may be embedded in free-text descriptions/merchant names.
/// </summary>
public interface IPiiRedactor
{
    /// <summary>
    /// Returns <paramref name="input"/> with every detected PII span replaced by a stable mask.
    /// Null/whitespace input returns <see cref="string.Empty"/> and never throws.
    /// </summary>
    string Redact(string? input);
}
