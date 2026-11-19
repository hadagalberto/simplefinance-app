using System.Text.Json;

namespace SimpleFinance.Application.Ai.Assistant;

/// <summary>
/// Tiny helper to author a tool's JSON-Schema as a literal string and materialize it into the
/// detached <see cref="JsonElement"/> that <see cref="LlmTool.Parameters"/> requires. Cloning the
/// root detaches it from the parsed <see cref="JsonDocument"/> so the element stays valid after the
/// document is disposed — safe to cache in a tool's static field.
/// </summary>
public static class ToolSchema
{
    public static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}
