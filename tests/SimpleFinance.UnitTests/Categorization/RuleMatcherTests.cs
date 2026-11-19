using System.Diagnostics;
using SimpleFinance.Application.Categorization;
using SimpleFinance.Domain.Entities;
using SimpleFinance.Domain.Enums;

namespace SimpleFinance.UnitTests.Categorization;

/// <summary>
/// Exercises RuleMatcher (04-04 Task 1) -- a pure, dependency-free static class implementing the
/// exact operator semantics from 04-04-PLAN.md's interfaces block, including REGEX safety
/// (100ms timeout + invalid-pattern tolerance, T-04-08's mitigation).
/// </summary>
public class RuleMatcherTests
{
    private static CategoryRule CreateRule(
        RuleMatchField field,
        RuleMatchOperator op,
        string? matchValue = null,
        decimal? amountMin = null,
        decimal? amountMax = null,
        int priority = 1)
    {
        var now = DateTime.UtcNow;
        return new CategoryRule
        {
            Id = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            MatchField = field,
            MatchOperator = op,
            MatchValue = matchValue,
            AmountMin = amountMin,
            AmountMax = amountMax,
            CategoryId = Guid.NewGuid(),
            Priority = priority,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    private static RuleMatchInput CreateInput(
        string description = "Padaria do Bairro",
        string? merchant = "Padaria do Bairro Ltda",
        decimal amount = -25.50m,
        string? pluggyCategory = "Food and Drink") =>
        new(description, merchant, amount, pluggyCategory);

    // --- CONTAINS ---

    [Fact]
    public void IsMatch_ContainsDescription_CaseInsensitiveSubstring_Matches()
    {
        var rule = CreateRule(RuleMatchField.Description, RuleMatchOperator.Contains, "PADARIA");
        var input = CreateInput(description: "Compra Padaria do Bairro");

        Assert.True(RuleMatcher.IsMatch(rule, input));
    }

    [Fact]
    public void IsMatch_ContainsDescription_NoSubstring_DoesNotMatch()
    {
        var rule = CreateRule(RuleMatchField.Description, RuleMatchOperator.Contains, "FARMACIA");
        var input = CreateInput(description: "Compra Padaria do Bairro");

        Assert.False(RuleMatcher.IsMatch(rule, input));
    }

    [Fact]
    public void IsMatch_ContainsMerchant_CaseInsensitiveSubstring_Matches()
    {
        var rule = CreateRule(RuleMatchField.Merchant, RuleMatchOperator.Contains, "padaria");
        var input = CreateInput(merchant: "Padaria do Bairro Ltda");

        Assert.True(RuleMatcher.IsMatch(rule, input));
    }

    [Fact]
    public void IsMatch_ContainsMerchant_NullMerchant_NeverMatches()
    {
        var rule = CreateRule(RuleMatchField.Merchant, RuleMatchOperator.Contains, "padaria");
        var input = CreateInput(merchant: null);

        Assert.False(RuleMatcher.IsMatch(rule, input));
    }

    [Fact]
    public void IsMatch_ContainsPluggyCategory_CaseInsensitiveSubstring_Matches()
    {
        var rule = CreateRule(RuleMatchField.PluggyCategory, RuleMatchOperator.Contains, "food");
        var input = CreateInput(pluggyCategory: "Food and Drink");

        Assert.True(RuleMatcher.IsMatch(rule, input));
    }

    [Fact]
    public void IsMatch_ContainsPluggyCategory_NoSubstring_DoesNotMatch()
    {
        var rule = CreateRule(RuleMatchField.PluggyCategory, RuleMatchOperator.Contains, "transport");
        var input = CreateInput(pluggyCategory: "Food and Drink");

        Assert.False(RuleMatcher.IsMatch(rule, input));
    }

    [Fact]
    public void IsMatch_ContainsAmount_NeverAValidCombination_DefensivelyReturnsFalse()
    {
        var rule = CreateRule(RuleMatchField.Amount, RuleMatchOperator.Contains, "25");
        var input = CreateInput(amount: -25.50m);

        Assert.False(RuleMatcher.IsMatch(rule, input));
    }

    // --- EQUALS ---

    [Fact]
    public void IsMatch_EqualsDescription_CaseInsensitiveExact_Matches()
    {
        var rule = CreateRule(RuleMatchField.Description, RuleMatchOperator.Equals, "padaria do bairro");
        var input = CreateInput(description: "Padaria do Bairro");

        Assert.True(RuleMatcher.IsMatch(rule, input));
    }

    [Fact]
    public void IsMatch_EqualsDescription_SubstringOnly_DoesNotMatch()
    {
        var rule = CreateRule(RuleMatchField.Description, RuleMatchOperator.Equals, "padaria");
        var input = CreateInput(description: "Compra Padaria do Bairro");

        Assert.False(RuleMatcher.IsMatch(rule, input));
    }

    [Fact]
    public void IsMatch_EqualsMerchant_NullMerchant_NeverMatches()
    {
        var rule = CreateRule(RuleMatchField.Merchant, RuleMatchOperator.Equals, "padaria do bairro ltda");
        var input = CreateInput(merchant: null);

        Assert.False(RuleMatcher.IsMatch(rule, input));
    }

    [Fact]
    public void IsMatch_EqualsPluggyCategory_CaseInsensitiveExact_Matches()
    {
        var rule = CreateRule(RuleMatchField.PluggyCategory, RuleMatchOperator.Equals, "FOOD AND DRINK");
        var input = CreateInput(pluggyCategory: "Food and Drink");

        Assert.True(RuleMatcher.IsMatch(rule, input));
    }

    [Fact]
    public void IsMatch_EqualsAmount_ParsesMatchValue_ComparesAbsoluteValue()
    {
        var rule = CreateRule(RuleMatchField.Amount, RuleMatchOperator.Equals, "25.50");
        var input = CreateInput(amount: -25.50m);

        Assert.True(RuleMatcher.IsMatch(rule, input));
    }

    [Fact]
    public void IsMatch_EqualsAmount_DifferentValue_DoesNotMatch()
    {
        var rule = CreateRule(RuleMatchField.Amount, RuleMatchOperator.Equals, "25.50");
        var input = CreateInput(amount: -99.99m);

        Assert.False(RuleMatcher.IsMatch(rule, input));
    }

    [Fact]
    public void IsMatch_EqualsAmount_UnparseableMatchValue_IsNoMatchNotThrow()
    {
        var rule = CreateRule(RuleMatchField.Amount, RuleMatchOperator.Equals, "not-a-decimal");
        var input = CreateInput(amount: -25.50m);

        var result = RuleMatcher.IsMatch(rule, input);

        Assert.False(result);
    }

    // --- REGEX ---

    [Fact]
    public void IsMatch_RegexDescription_PatternMatches()
    {
        var rule = CreateRule(RuleMatchField.Description, RuleMatchOperator.Regex, "^Padaria.*Bairro$");
        var input = CreateInput(description: "Padaria do Bairro");

        Assert.True(RuleMatcher.IsMatch(rule, input));
    }

    [Fact]
    public void IsMatch_RegexDescription_PatternDoesNotMatch()
    {
        var rule = CreateRule(RuleMatchField.Description, RuleMatchOperator.Regex, "^Farmacia.*$");
        var input = CreateInput(description: "Padaria do Bairro");

        Assert.False(RuleMatcher.IsMatch(rule, input));
    }

    [Fact]
    public void IsMatch_RegexMerchant_NullMerchant_NeverMatches()
    {
        var rule = CreateRule(RuleMatchField.Merchant, RuleMatchOperator.Regex, "^Padaria.*$");
        var input = CreateInput(merchant: null);

        Assert.False(RuleMatcher.IsMatch(rule, input));
    }

    [Fact]
    public void IsMatch_RegexPluggyCategory_PatternMatches()
    {
        var rule = CreateRule(RuleMatchField.PluggyCategory, RuleMatchOperator.Regex, "(?i)^food.*$");
        var input = CreateInput(pluggyCategory: "Food and Drink");

        Assert.True(RuleMatcher.IsMatch(rule, input));
    }

    [Fact]
    public void IsMatch_RegexCatastrophicBacktracking_ResolvesToNoMatchWithinTimeout()
    {
        // Classic ReDoS pattern: nested quantifiers against a long non-matching string trigger
        // catastrophic backtracking without the 100ms Regex.IsMatch matchTimeout (T-04-08).
        var rule = CreateRule(RuleMatchField.Description, RuleMatchOperator.Regex, "^(a+)+$");
        var input = CreateInput(description: new string('a', 40) + "!");

        var stopwatch = Stopwatch.StartNew();
        var result = RuleMatcher.IsMatch(rule, input);
        stopwatch.Stop();

        Assert.False(result);
        Assert.True(stopwatch.ElapsedMilliseconds < 1000,
            $"Expected the 100ms Regex timeout to engage; took {stopwatch.ElapsedMilliseconds}ms instead.");
    }

    [Fact]
    public void IsMatch_RegexInvalidPattern_IsNoMatchNotThrow()
    {
        var rule = CreateRule(RuleMatchField.Description, RuleMatchOperator.Regex, "(unbalanced(paren");
        var input = CreateInput();

        var result = RuleMatcher.IsMatch(rule, input);

        Assert.False(result);
    }

    // --- RANGE ---

    [Fact]
    public void IsMatch_RangeAmount_WithinInclusiveBounds_Matches()
    {
        var rule = CreateRule(RuleMatchField.Amount, RuleMatchOperator.Range, amountMin: 10m, amountMax: 50m);
        var input = CreateInput(amount: -25.50m);

        Assert.True(RuleMatcher.IsMatch(rule, input));
    }

    [Fact]
    public void IsMatch_RangeAmount_AtInclusiveBoundaries_Matches()
    {
        var ruleMin = CreateRule(RuleMatchField.Amount, RuleMatchOperator.Range, amountMin: 25.50m, amountMax: 50m);
        var ruleMax = CreateRule(RuleMatchField.Amount, RuleMatchOperator.Range, amountMin: 10m, amountMax: 25.50m);
        var input = CreateInput(amount: -25.50m);

        Assert.True(RuleMatcher.IsMatch(ruleMin, input));
        Assert.True(RuleMatcher.IsMatch(ruleMax, input));
    }

    [Fact]
    public void IsMatch_RangeAmount_OutsideBounds_DoesNotMatch()
    {
        var rule = CreateRule(RuleMatchField.Amount, RuleMatchOperator.Range, amountMin: 100m, amountMax: 200m);
        var input = CreateInput(amount: -25.50m);

        Assert.False(RuleMatcher.IsMatch(rule, input));
    }

    // --- SelectFirstMatch ---

    [Fact]
    public void SelectFirstMatch_ReturnsFirstMatchingRuleInOrder()
    {
        var rule1 = CreateRule(RuleMatchField.Description, RuleMatchOperator.Contains, "padaria", priority: 1);
        var rule2 = CreateRule(RuleMatchField.Description, RuleMatchOperator.Contains, "padaria", priority: 2);
        var input = CreateInput(description: "Compra na Padaria");

        var matched = RuleMatcher.SelectFirstMatch([rule1, rule2], input);

        Assert.Same(rule1, matched);
    }

    [Fact]
    public void SelectFirstMatch_StopsEvaluatingAfterFirstMatch_NeverEvaluatesLaterRules()
    {
        // rule1 matches and should be returned. rule2 is deliberately malformed (Range with both
        // bounds null) -- if SelectFirstMatch continued evaluating past the first match, calling
        // IsMatch on rule2 would throw (Value on a null Nullable<decimal>). Its absence proves
        // rule2 was never evaluated.
        var rule1 = CreateRule(RuleMatchField.Description, RuleMatchOperator.Contains, "padaria", priority: 1);
        var rule2 = CreateRule(RuleMatchField.Amount, RuleMatchOperator.Range, amountMin: null, amountMax: null, priority: 2);
        var input = CreateInput(description: "Compra na Padaria");

        var matched = RuleMatcher.SelectFirstMatch([rule1, rule2], input);

        Assert.Same(rule1, matched);
    }

    [Fact]
    public void SelectFirstMatch_NoRuleMatches_ReturnsNull()
    {
        var rule1 = CreateRule(RuleMatchField.Description, RuleMatchOperator.Contains, "farmacia", priority: 1);
        var rule2 = CreateRule(RuleMatchField.Description, RuleMatchOperator.Contains, "mercado", priority: 2);
        var input = CreateInput(description: "Compra na Padaria");

        var matched = RuleMatcher.SelectFirstMatch([rule1, rule2], input);

        Assert.Null(matched);
    }
}
