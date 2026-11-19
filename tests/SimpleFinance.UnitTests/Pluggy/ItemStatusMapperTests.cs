using SimpleFinance.Domain.Enums;
using SimpleFinance.Infrastructure.Pluggy;

namespace SimpleFinance.UnitTests.Pluggy;

/// <summary>
/// Covers PLG-04 — forces each of the 5 documented Connection.Status values (plus the
/// WAITING_USER_INPUT vs LOGIN_ERROR distinction) per the phase's own Definition of Done
/// (02-CONTEXT.md's Specifics section).
/// </summary>
public class ItemStatusMapperTests
{
    public static IEnumerable<object[]> TransitiveExecutionStatuses =>
    [
        ["CREATED"],
        ["LOGIN_IN_PROGRESS"],
        ["LOGIN_MFA_IN_PROGRESS"],
        ["ACCOUNTS_IN_PROGRESS"],
        ["CREDITCARDS_IN_PROGRESS"],
        ["TRANSACTIONS_IN_PROGRESS"],
        ["INVESTMENT_TRANSACTIONS_IN_PROGRESS"],
        ["PAYMENT_DATA_IN_PROGRESS"],
        ["IDENTITY_IN_PROGRESS"],
        ["MERGING"],
    ];

    [Theory]
    [MemberData(nameof(TransitiveExecutionStatuses))]
    public void Map_StatusUpdatingWithAnyTransitiveExecutionStatus_ReturnsWaitForCompletion(string executionStatus)
    {
        var result = ItemStatusMapper.Map(ConnectionStatus.Updating, executionStatus);

        Assert.Equal(ConnectionSuggestedAction.WaitForCompletion, result);
    }

    [Fact]
    public void Map_StatusUpdatedWithExecutionStatusSuccess_ReturnsNone()
    {
        var result = ItemStatusMapper.Map(ConnectionStatus.Updated, "SUCCESS");

        Assert.Equal(ConnectionSuggestedAction.None, result);
    }

    [Fact]
    public void Map_StatusUpdatedWithExecutionStatusPartialSuccess_ReturnsReviewPartialFailure()
    {
        var result = ItemStatusMapper.Map(ConnectionStatus.Updated, "PARTIAL_SUCCESS");

        Assert.Equal(ConnectionSuggestedAction.ReviewPartialFailure, result);
    }

    [Fact]
    public void Map_StatusWaitingUserInput_ReturnsProvideMfaDistinctFromReconnectRequired()
    {
        var result = ItemStatusMapper.Map(ConnectionStatus.WaitingUserInput, "WAITING_USER_INPUT");

        Assert.Equal(ConnectionSuggestedAction.ProvideMfa, result);
        Assert.NotEqual(ConnectionSuggestedAction.ReconnectRequired, result);
    }

    [Fact]
    public void Map_StatusLoginError_ReturnsReconnectRequired()
    {
        var result = ItemStatusMapper.Map(ConnectionStatus.LoginError, "INVALID_CREDENTIALS");

        Assert.Equal(ConnectionSuggestedAction.ReconnectRequired, result);
    }

    [Theory]
    [InlineData("INVALID_CREDENTIALS")]
    [InlineData("INVALID_CREDENTIALS_MFA")]
    public void Map_ExecutionStatusStartingWithInvalidCredentials_ReturnsReconnectRequiredEvenUnderOutdatedStatus(string executionStatus)
    {
        var result = ItemStatusMapper.Map(ConnectionStatus.Outdated, executionStatus);

        Assert.Equal(ConnectionSuggestedAction.ReconnectRequired, result);
    }

    [Fact]
    public void Map_StatusOutdatedWithUnmappedExecutionStatus_ReturnsGenericRetryAndNeverThrows()
    {
        var result = ItemStatusMapper.Map(ConnectionStatus.Outdated, "SOME_BRAND_NEW_PLUGGY_ERROR_CODE_NOT_YET_DOCUMENTED");

        Assert.Equal(ConnectionSuggestedAction.GenericRetry, result);
    }

    [Fact]
    public void Map_StatusOutdatedWithNullExecutionStatus_ReturnsGenericRetryAndNeverThrows()
    {
        var result = ItemStatusMapper.Map(ConnectionStatus.Outdated, executionStatus: null);

        Assert.Equal(ConnectionSuggestedAction.GenericRetry, result);
    }
}
