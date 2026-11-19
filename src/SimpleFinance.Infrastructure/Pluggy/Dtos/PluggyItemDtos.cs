using System.Text.Json.Serialization;

namespace SimpleFinance.Infrastructure.Pluggy.Dtos;

/// <summary>
/// POST /connect_token request body (ItemOptions-shaped). webhookUrl is deliberately OMITTED —
/// a single global webhook subscription is registered once via PluggyClient.EnsureWebhookRegisteredAsync
/// (02-RESEARCH.md Pattern 2), avoiding Pitfall 15's duplicate-per-item-subscription trap.
/// ItemId is set only when reconnecting an existing Item (update mode, PLG-05).
/// </summary>
public sealed record PluggyConnectTokenRequest(
    [property: JsonPropertyName("itemId")] string? ItemId,
    [property: JsonPropertyName("clientUserId")] string? ClientUserId,
    [property: JsonPropertyName("oauthRedirectUrl")] string? OAuthRedirectUrl,
    [property: JsonPropertyName("avoidDuplicates")] bool? AvoidDuplicates);

/// <summary>
/// POST /connect_token response body. Field name "accessToken" per Pluggy's Connect Token API —
/// 02-RESEARCH.md did not independently re-verify the exact wire key this session (Assumption,
/// LOW risk since CONTEXT.md routes the widget flow through this token opaquely); verify against
/// a live sandbox POST /connect_token call before relying on this in production.
/// </summary>
public sealed record PluggyConnectTokenResponse(
    [property: JsonPropertyName("accessToken")] string AccessToken);

public sealed record PluggyConnectorDto(
    [property: JsonPropertyName("id")] int? Id,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("imageUrl")] string? ImageUrl,
    [property: JsonPropertyName("institutionUrl")] string? InstitutionUrl);

/// <summary>
/// GET /items/{id} response. Modeled loosely/defensively (nullable fields) since the exact
/// nested shape is MEDIUM confidence per 02-RESEARCH.md — Pluggy may add fields without notice.
/// </summary>
public sealed record PluggyItemResponse(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("connector")] PluggyConnectorDto? Connector,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("executionStatus")] string? ExecutionStatus,
    [property: JsonPropertyName("statusDetail")] string? StatusDetail,
    [property: JsonPropertyName("error")] PluggyItemErrorDto? Error);

public sealed record PluggyItemErrorDto(
    [property: JsonPropertyName("code")] string? Code,
    [property: JsonPropertyName("message")] string? Message);

/// <summary>
/// PATCH /items/{id} request body — used both for plain re-sync triggers (Parameters null) and
/// credential updates on reconnect (Parameters populated, single-use MFA token per 02-RESEARCH.md's
/// "must send a different MFA than the last execution" note).
/// </summary>
public sealed record PluggyItemPatchRequest(
    [property: JsonPropertyName("parameters")] IReadOnlyDictionary<string, string>? Parameters,
    [property: JsonPropertyName("clientUserId")] string? ClientUserId,
    [property: JsonPropertyName("webhookUrl")] string? WebhookUrl,
    [property: JsonPropertyName("products")] string[]? Products);
