namespace SimpleFinance.Domain.Enums;

/// <summary>Pluggy Account <c>type</c> field.</summary>
public enum AccountType
{
    Bank,
    Credit
}

/// <summary>Pluggy Account <c>subtype</c> field.</summary>
public enum AccountSubtype
{
    CheckingAccount,
    SavingsAccount,
    CreditCard
}

/// <summary>
/// Pluggy Account's nested <c>creditData.status</c> field — new field not in PRD §11.3's
/// original <c>CreditCardDetail</c> schema, added per 02-RESEARCH.md's Data Endpoints finding
/// (cheap to add now, useful for surfacing a blocked/cancelled card distinctly rather than a
/// generic sync error).
/// </summary>
public enum CreditCardStatus
{
    Active,
    Blocked,
    Cancelled
}
