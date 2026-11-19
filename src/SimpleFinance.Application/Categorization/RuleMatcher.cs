using System.Globalization;
using System.Text.RegularExpressions;
using SimpleFinance.Domain.Entities;
using SimpleFinance.Domain.Enums;

namespace SimpleFinance.Application.Categorization;

/// <summary>
/// The transaction fields a <see cref="CategoryRule"/> can be matched against, per 04-04-PLAN.md's
/// interfaces block. Deliberately decoupled from <see cref="Sync.CategorizationInput"/> (the
/// Infrastructure-facing seam type) so this class stays free of any Sync/Infrastructure
/// dependency -- 04-05's rule test/apply-now endpoints construct this record directly.
///
/// <c>PluggyCategory</c> carries the human-readable category NAME (Transaction.PluggyCategory,
/// e.g. "Food and Drink"), never the opaque PluggyCategoryId code -- the plan's own operator
/// semantics are explicit that PluggyCategory-field rules must match "the human-readable name
/// field, not PluggyCategoryId... friendlier for a user writing rules". Matching against the raw
/// id code would make every PluggyCategory-field rule silently unusable for end users.
/// </summary>
public sealed record RuleMatchInput(string Description, string? MerchantName, decimal Amount, string? PluggyCategory);

/// <summary>
/// Pure, I/O-free rule matching (04-04 Task 1). No DbContext, no I/O -- fully unit-testable in
/// isolation, and reused as-is by 04-05's "test rule"/"apply now" endpoints so the matching
/// definition can never drift between automatic categorization and those retroactive UI actions.
///
/// REGEX safety (T-04-08): every Regex.IsMatch call uses an explicit 100ms matchTimeout and is
/// wrapped in try/catch for both RegexParseException (malformed pattern) and
/// RegexMatchTimeoutException (catastrophic backtracking) -- both are treated as no-match, never
/// rethrown, so a single bad user-authored rule can never hang or crash categorization.
/// </summary>
public static class RuleMatcher
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);

    public static bool IsMatch(CategoryRule rule, RuleMatchInput input)
    {
        if (rule.MatchField == RuleMatchField.Amount)
        {
            return rule.MatchOperator switch
            {
                RuleMatchOperator.Equals => MatchAmountEquals(rule, input),
                RuleMatchOperator.Range => MatchAmountRange(rule, input),
                // CONTAINS/REGEX are never valid on Amount (04-05's validator blocks this at
                // creation time) -- defensively treated as no-match here rather than throwing.
                _ => false,
            };
        }

        // RANGE is Amount-only; a RANGE rule targeting a text field is not a valid combination
        // (defensive no-match, never thrown).
        if (rule.MatchOperator == RuleMatchOperator.Range)
        {
            return false;
        }

        var fieldValue = GetFieldValue(rule.MatchField, input);
        if (fieldValue is null)
        {
            return false;
        }

        return rule.MatchOperator switch
        {
            RuleMatchOperator.Contains => rule.MatchValue is not null
                && fieldValue.Contains(rule.MatchValue, StringComparison.OrdinalIgnoreCase),
            RuleMatchOperator.Equals => rule.MatchValue is not null
                && string.Equals(fieldValue, rule.MatchValue, StringComparison.OrdinalIgnoreCase),
            RuleMatchOperator.Regex => MatchRegex(fieldValue, rule.MatchValue),
            _ => false,
        };
    }

    /// <summary>
    /// Rules MUST already be ordered ascending by Priority before calling this -- the function
    /// itself does not sort, keeping it a pure single-pass predicate over caller-supplied order.
    /// Returns the first rule whose IsMatch is true, short-circuiting via LINQ's deferred
    /// FirstOrDefault (later rules are never evaluated once an earlier one matches).
    /// </summary>
    public static CategoryRule? SelectFirstMatch(IEnumerable<CategoryRule> rulesOrderedByPriority, RuleMatchInput input)
    {
        foreach (var rule in rulesOrderedByPriority)
        {
            if (IsMatch(rule, input))
            {
                return rule;
            }
        }

        return null;
    }

    private static string? GetFieldValue(RuleMatchField field, RuleMatchInput input) => field switch
    {
        RuleMatchField.Description => input.Description,
        RuleMatchField.Merchant => input.MerchantName,
        RuleMatchField.PluggyCategory => input.PluggyCategory,
        _ => null,
    };

    private static bool MatchAmountEquals(CategoryRule rule, RuleMatchInput input)
    {
        if (rule.MatchValue is null
            || !decimal.TryParse(rule.MatchValue, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsedValue))
        {
            return false;
        }

        return Math.Abs(input.Amount) == parsedValue;
    }

    private static bool MatchAmountRange(CategoryRule rule, RuleMatchInput input)
    {
        if (rule.AmountMin is null || rule.AmountMax is null)
        {
            return false;
        }

        var absAmount = Math.Abs(input.Amount);
        return absAmount >= rule.AmountMin.Value && absAmount <= rule.AmountMax.Value;
    }

    private static bool MatchRegex(string fieldValue, string? pattern)
    {
        if (pattern is null)
        {
            return false;
        }

        try
        {
            return Regex.IsMatch(fieldValue, pattern, RegexOptions.IgnoreCase, RegexTimeout);
        }
        catch (RegexParseException)
        {
            // Malformed pattern -- treated as no-match, never rethrown (T-04-08).
            return false;
        }
        catch (RegexMatchTimeoutException)
        {
            // Catastrophic backtracking -- treated as no-match, never rethrown (T-04-08).
            return false;
        }
    }
}
