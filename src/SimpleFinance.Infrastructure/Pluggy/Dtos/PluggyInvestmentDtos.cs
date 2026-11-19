using System.Text.Json.Serialization;

namespace SimpleFinance.Infrastructure.Pluggy.Dtos;

/// <summary>
/// GET /investments?itemId= response item shape, per 02-RESEARCH.md's verified field list
/// (schema reconstructed via WebSearch summary, MEDIUM confidence).
/// </summary>
public sealed record PluggyInvestmentDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("number")] string? Number,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("balance")] decimal? Balance,
    [property: JsonPropertyName("currencyCode")] string? CurrencyCode,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("subtype")] string? Subtype,
    [property: JsonPropertyName("code")] string? Code,
    [property: JsonPropertyName("isin")] string? Isin,
    [property: JsonPropertyName("value")] decimal? Value,
    [property: JsonPropertyName("quantity")] decimal? Quantity,
    [property: JsonPropertyName("amount")] decimal? Amount,
    [property: JsonPropertyName("taxes")] decimal? Taxes,
    [property: JsonPropertyName("date")] DateTime? Date,
    [property: JsonPropertyName("dueDate")] DateTime? DueDate,
    [property: JsonPropertyName("owner")] string? Owner,
    [property: JsonPropertyName("amountProfit")] decimal? AmountProfit,
    [property: JsonPropertyName("amountWithdrawal")] decimal? AmountWithdrawal,
    [property: JsonPropertyName("amountOriginal")] decimal? AmountOriginal,
    [property: JsonPropertyName("status")] string? Status,
    [property: JsonPropertyName("issuer")] string? Issuer,
    [property: JsonPropertyName("issueDate")] DateTime? IssueDate,
    [property: JsonPropertyName("lastMonthRate")] decimal? LastMonthRate,
    [property: JsonPropertyName("lastTwelveMonthsRate")] decimal? LastTwelveMonthsRate,
    [property: JsonPropertyName("annualRate")] decimal? AnnualRate,
    [property: JsonPropertyName("rate")] decimal? Rate,
    [property: JsonPropertyName("rateType")] string? RateType,
    [property: JsonPropertyName("fixedAnnualRate")] decimal? FixedAnnualRate,
    [property: JsonPropertyName("itemId")] string ItemId);
