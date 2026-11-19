using System.Text.Json;

namespace SimpleFinance.Infrastructure.Ai.Assistant.Tools;

/// <summary>
/// Shared helpers for the read tools' compact JSON results. Errors are returned as a small
/// <c>{ "error": ..., "message"?: ... }</c> object (never thrown) so the orchestrator's tool loop can
/// narrate the failure to the user instead of breaking — the same discipline the dispatcher enforces
/// for unknown tools (T-17-03).
/// </summary>
internal static class ToolResults
{
    public static string Error(string error, string? message = null) =>
        message is null
            ? JsonSerializer.Serialize(new { error })
            : JsonSerializer.Serialize(new { error, message });
}
