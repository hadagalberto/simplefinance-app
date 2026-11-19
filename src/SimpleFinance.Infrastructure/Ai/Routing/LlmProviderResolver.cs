using SimpleFinance.Domain.Enums;

namespace SimpleFinance.Infrastructure.Ai.Routing;

/// <summary>
/// Pure model-name → chat provider rule. There is no separate "provider" setting: the user switches
/// provider by typing a model name in Settings → IA.
/// <list type="bullet">
///   <item><c>claude-*</c> (case-insensitive) → <see cref="AiProvider.Anthropic"/>;</item>
///   <item>any name containing <c>/</c> (OpenRouter <c>vendor/model</c> slugs, e.g.
///   <c>anthropic/claude-sonnet-5-5</c>) → <see cref="AiProvider.OpenRouter"/>. Checked first, so a
///   slug always goes to OpenRouter even when its model part starts with <c>claude-</c>;</item>
///   <item>anything else → <see cref="AiProvider.DeepSeek"/> (the historical default).</item>
/// </list>
/// </summary>
public static class LlmProviderResolver
{
    public static AiProvider Resolve(string? model)
    {
        var name = model?.Trim() ?? string.Empty;

        if (name.Contains('/'))
        {
            return AiProvider.OpenRouter;
        }

        if (name.StartsWith("claude-", StringComparison.OrdinalIgnoreCase))
        {
            return AiProvider.Anthropic;
        }

        return AiProvider.DeepSeek;
    }
}
