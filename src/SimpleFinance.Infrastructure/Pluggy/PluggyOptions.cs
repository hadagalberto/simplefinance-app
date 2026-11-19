namespace SimpleFinance.Infrastructure.Pluggy;

/// <summary>
/// Binds to the "Pluggy" configuration section (Pluggy__* — see .env.example). Server-to-server
/// credentials only; never exposed to the browser (02-CONTEXT.md, docs.pluggy.ai's own warning
/// that ClientId/ClientSecret are "extremely sensitive").
/// </summary>
public class PluggyOptions
{
    public const string SectionName = "Pluggy";

    public string ClientId { get; set; } = string.Empty;

    public string ClientSecret { get; set; } = string.Empty;

    /// <summary>"sandbox" (default, PLG-07) or "production".</summary>
    public string Environment { get; set; } = "sandbox";

    /// <summary>
    /// Public HTTPS URL Pluggy delivers webhook events to. Only used by the one-time global
    /// `POST /webhooks` registration (Pattern 2, 02-RESEARCH.md) — never passed per Connect
    /// Token (Pitfall 15's duplicate-subscription trap).
    /// </summary>
    public string? WebhookPublicUrl { get; set; }

    /// <summary>
    /// Custom HTTP header name Pluggy echoes back on every webhook call, used to authenticate
    /// inbound requests (no HMAC scheme exists per 02-RESEARCH.md — this + IP allowlist are the
    /// only documented mechanisms).
    /// </summary>
    public string WebhookSecretHeaderName { get; set; } = "X-Webhook-Secret";

    public string? WebhookSecretValue { get; set; }

    /// <summary>
    /// IP allowlist for inbound Pluggy webhook calls. Configurable, NOT gospel — 02-RESEARCH.md
    /// documents a single IP (52.67.145.81) as of 2026-07-10, but explicitly flags this for
    /// re-verification against the live Pluggy dashboard at deploy time (CONTEXT.md's TODO).
    /// </summary>
    public string[] WebhookAllowedIps { get; set; } = ["52.67.145.81"];

    /// <summary>
    /// Pluggy API base URL. Confirmed at deploy time (live 404 from the assumed
    /// "api-sandbox.pluggy.ai" host): Pluggy exposes a SINGLE base URL —
    /// https://api.pluggy.ai — for BOTH sandbox and production. Sandbox vs production is
    /// selected by which connectors are used (sandbox connector IDs / the Connect widget's
    /// sandbox mode), not by a distinct hostname. Environment is retained for connector
    /// selection and future use, but does not change the base URL.
    /// </summary>
    public string BaseUrl => "https://api.pluggy.ai";
}
