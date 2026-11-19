using System.Text.Json;
using System.Text.Json.Serialization;

namespace SimpleFinance.Infrastructure.Pluggy.Dtos;

/// <summary>
/// GET /v2/transactions response item shape, per 02-RESEARCH.md's verified field list.
/// Category/CategoryId/MerchantName/MerchantDocument are all nullable -- these are Pro-gated
/// per Pluggy's docs (Open Question flagged in RESEARCH.md), so a null value here is expected
/// and must never throw during mapping (PluggyDataProvider, 02-03 Task 2).
/// </summary>
public sealed record PluggyTransactionDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("date")] DateTime Date,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("descriptionRaw")] string? DescriptionRaw,
    [property: JsonPropertyName("amount")] decimal Amount,
    [property: JsonPropertyName("amountInAccountCurrency")] decimal? AmountInAccountCurrency,
    [property: JsonPropertyName("balance")] decimal? Balance,
    [property: JsonPropertyName("currencyCode")] string CurrencyCode,
    [property: JsonPropertyName("category")] string? Category,
    [property: JsonPropertyName("categoryId")] string? CategoryId,
    [property: JsonPropertyName("accountId")] string AccountId,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("providerCode")] string? ProviderCode,
    [property: JsonPropertyName("providerId")] string? ProviderId,
    // Opaque passthrough (threat T-02-08, accept disposition) -- never parsed/typed here, only
    // ever raw-text-serialized downstream for Phase 3's transfer-detection consumption.
    [property: JsonPropertyName("paymentData")] JsonElement? PaymentData,
    [property: JsonPropertyName("creditCardMetadata")] PluggyCreditCardMetadataDto? CreditCardMetadata,
    [property: JsonPropertyName("merchantName")] string? MerchantName,
    [property: JsonPropertyName("merchantDocument")] string? MerchantDocument,
    [property: JsonPropertyName("operationType")] string? OperationType);

public sealed record PluggyCreditCardMetadataDto(
    [property: JsonPropertyName("installmentNumber")] int? InstallmentNumber,
    [property: JsonPropertyName("totalInstallments")] int? TotalInstallments,
    [property: JsonPropertyName("totalAmount")] decimal? TotalAmount,
    [property: JsonPropertyName("billId")] string? BillId);

/// <summary>
/// The v2 cursor envelope: Next is the full next-page query string (e.g.
/// "?accountId=X&amp;after=cursor123") per 02-RESEARCH.md's Pattern 1 -- PluggyClient follows it
/// verbatim, never hand-constructing the "after" cursor value itself.
/// </summary>
public sealed record PluggyTransactionsPageDto(
    [property: JsonPropertyName("results")] IReadOnlyList<PluggyTransactionDto> Results,
    [property: JsonPropertyName("next")] string? Next);
