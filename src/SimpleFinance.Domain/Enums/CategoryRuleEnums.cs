namespace SimpleFinance.Domain.Enums;

/// <summary>The transaction field a CategoryRule matches against, per PRD CAT-03.</summary>
public enum RuleMatchField
{
    Description,
    Merchant,
    PluggyCategory,
    Amount
}

/// <summary>The comparison operator a CategoryRule applies to its MatchField, per PRD CAT-03.</summary>
public enum RuleMatchOperator
{
    Contains,
    Equals,
    Regex,
    Range
}
