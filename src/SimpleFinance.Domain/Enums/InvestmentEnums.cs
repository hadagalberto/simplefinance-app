namespace SimpleFinance.Domain.Enums;

/// <summary>
/// Pluggy Investment <c>type</c> field — matches PRD §6.9/§11.7 and 02-RESEARCH.md's verified
/// investment <c>type</c> field exactly.
/// </summary>
public enum InvestmentType
{
    MutualFund,
    FixedIncome,
    Equity,
    Etf,
    Security,
    Coe,
    Crypto
}
