using System.Text.Json.Serialization;

namespace SimpleFinance.Infrastructure.Ai.OpenAi.Dtos;

/// <summary>
/// PRIVATE OpenAI wire DTOs for <c>POST /embeddings</c> (15-RESEARCH §OpenAI embeddings shapes).
/// These on-the-wire JSON shapes MUST NOT leak past Infrastructure — <see cref="OpenAiEmbeddingClient"/>
/// maps them into the neutral <c>float[][]</c> result. <c>dimensions</c> is omitted so the model
/// returns its native 1536-length vectors (CONTEXT lock); <c>encoding_format</c> is <c>float</c>.
/// The response <c>data[]</c> carries an <c>index</c> that MUST be sorted on before projecting —
/// array order is not guaranteed (RESEARCH Pitfall 4 / T-15-13).
/// </summary>
internal sealed record OpenAiEmbeddingRequest(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("input")] IReadOnlyList<string> Input,
    [property: JsonPropertyName("encoding_format")] string EncodingFormat = "float",
    [property: JsonPropertyName("dimensions")] int? Dimensions = null);

internal sealed record OpenAiEmbeddingResponse(
    [property: JsonPropertyName("data")] IReadOnlyList<OpenAiEmbeddingData> Data,
    [property: JsonPropertyName("usage")] OpenAiEmbeddingUsage? Usage);

internal sealed record OpenAiEmbeddingData(
    [property: JsonPropertyName("index")] int Index,
    [property: JsonPropertyName("embedding")] float[] Embedding);

internal sealed record OpenAiEmbeddingUsage(
    [property: JsonPropertyName("prompt_tokens")] int PromptTokens,
    [property: JsonPropertyName("total_tokens")] int TotalTokens);
