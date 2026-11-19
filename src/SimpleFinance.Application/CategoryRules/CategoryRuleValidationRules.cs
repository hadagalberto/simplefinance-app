using System.Globalization;
using System.Text.RegularExpressions;
using SimpleFinance.Domain.Enums;

namespace SimpleFinance.Application.CategoryRules;

/// <summary>
/// Shared field/operator/value combination rules for CategoryRule create/update/test-rule
/// validators (04-05) -- centralized so write-time validation semantics can never drift across
/// the three call sites (a rule accepted by Create must behave identically when re-validated by
/// Update, and Test's draft-rule shape must accept exactly the same combinations a real saved
/// rule would). Runtime matching itself stays in RuleMatcher (04-04) -- this class only decides
/// what counts as a WELL-FORMED rule at write time, never how a rule matches a transaction.
/// </summary>
public static class CategoryRuleValidationRules
{
    private const int MaxRegexPatternLength = 200;

    public static bool TryParseFields(string matchField, string matchOperator, out RuleMatchField field, out RuleMatchOperator op)
    {
        var fieldOk = Enum.TryParse(matchField, ignoreCase: true, out field);
        var opOk = Enum.TryParse(matchOperator, ignoreCase: true, out op);
        return fieldOk && opOk;
    }

    /// <summary>
    /// Amount field only allows Equals/Range (never Contains/Regex): Range requires both
    /// AmountMin/AmountMax non-null with AmountMin &lt;= AmountMax; Equals on Amount requires a
    /// MatchValue that parses as an invariant-culture decimal (mirrors RuleMatcher's own
    /// MatchAmountEquals parse -- catching an unparseable value at write time instead of saving a
    /// rule that would silently never match, same rationale as the REGEX pre-compile check
    /// below). Non-Amount fields only allow Contains/Equals/Regex (never Range) with a non-empty
    /// MatchValue.
    /// </summary>
    public static bool HasValidFieldOperatorCombination(
        RuleMatchField field, RuleMatchOperator op, string? matchValue, decimal? amountMin, decimal? amountMax)
    {
        if (field == RuleMatchField.Amount)
        {
            return op switch
            {
                RuleMatchOperator.Range => amountMin is not null && amountMax is not null && amountMin <= amountMax,
                RuleMatchOperator.Equals => !string.IsNullOrWhiteSpace(matchValue)
                    && decimal.TryParse(matchValue, NumberStyles.Number, CultureInfo.InvariantCulture, out _),
                _ => false,
            };
        }

        return op != RuleMatchOperator.Range && !string.IsNullOrWhiteSpace(matchValue);
    }

    public static bool IsRegexOperator(RuleMatchOperator op) => op == RuleMatchOperator.Regex;

    public static bool ExceedsRegexMaxLength(string? matchValue) => matchValue is not null && matchValue.Length > MaxRegexPatternLength;

    /// <summary>
    /// Rejects a REGEX pattern that fails to compile at write time (T-04-08 defense-in-depth) --
    /// a null pattern is treated as valid here since NotEmpty-style presence is already enforced
    /// by <see cref="HasValidFieldOperatorCombination"/>.
    /// </summary>
    public static bool IsValidRegexPattern(string? pattern)
    {
        if (pattern is null)
        {
            return true;
        }

        try
        {
            _ = new Regex(pattern);
            return true;
        }
        catch (RegexParseException)
        {
            return false;
        }
    }
}
