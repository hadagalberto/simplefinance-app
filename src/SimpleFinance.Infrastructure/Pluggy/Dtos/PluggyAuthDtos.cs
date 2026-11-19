using System.Text.Json.Serialization;

namespace SimpleFinance.Infrastructure.Pluggy.Dtos;

/// <summary>POST /auth request body. System.Text.Json throughout — never Newtonsoft (02-CONTEXT.md's locked decision).</summary>
public sealed record PluggyAuthRequest(
    [property: JsonPropertyName("clientId")] string ClientId,
    [property: JsonPropertyName("clientSecret")] string ClientSecret);

/// <summary>POST /auth response body. Field name confirmed "apiKey" per docs.pluggy.ai (02-RESEARCH.md, HIGH confidence).</summary>
public sealed record PluggyAuthResponse(
    [property: JsonPropertyName("apiKey")] string ApiKey);
