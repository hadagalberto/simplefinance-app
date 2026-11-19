namespace SimpleFinance.Application.Ai.Usage;

/// <summary>
/// AI-FND-05: the pre-flight "Denial-of-Wallet" kill-switch the provider clients (15-05) call
/// between PII redaction and the outbound HTTP request (redact → budget-check → send → record).
/// Sums the user's month-to-date estimated AI spend and compares it to
/// <c>UserSettings.AiMonthlyBudgetUsd</c>; when the cap is reached it throws
/// <see cref="AiBudgetExceededException"/> so callers degrade gracefully (categorization →
/// rules-only, assistant → "orçamento de IA do mês atingido") instead of making a cloud call.
/// A cap of 0 blocks the very first call ⇒ zero cloud calls while the app stays fully functional.
/// Defined in Application (the seam callers depend on); implemented in Infrastructure over the
/// DbContext.
/// </summary>
public interface IAiBudgetGuard
{
    /// <summary>
    /// Throws <see cref="AiBudgetExceededException"/> when <paramref name="userId"/>'s month-to-date
    /// AI spend is at or above their configured monthly budget; returns normally otherwise. Fires
    /// exactly one budget-cap notification per calendar month the moment the cap is first crossed.
    /// </summary>
    Task EnsureWithinBudgetAsync(Guid userId, CancellationToken ct);
}
