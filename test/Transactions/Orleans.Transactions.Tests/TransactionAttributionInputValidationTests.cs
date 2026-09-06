using System.Collections.Generic;
using Orleans.Transactions.TestKit;
using TestExtensions;
using Xunit;

namespace Orleans.Transactions.Tests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Transactions")]
[TestCategory("BVT"), TestCategory("Transactions")]
public class TransactionAttributionInputValidationTests
{
    public static TheoryData<string> AttributionGrainCases => new()
    {
        nameof(NoAttributionGrain),
        nameof(SuppressAttributionGrain),
        nameof(CreateOrJoinAttributionGrain),
        nameof(CreateAttributionGrain),
        nameof(JoinAttributionGrain),
        nameof(SupportedAttributionGrain),
        nameof(NotAllowedAttributionGrain),
    };

    [Theory]
    [MemberData(nameof(AttributionGrainCases))]
    public void GetNestedTransactionIds_NullTiers_ThrowsWithTiersParamNameBeforeRuntimeAccess(
        string grainCase)
    {
        var exception = Assert.Throws<ArgumentNullException>(() =>
        {
            _ = InvokeGetNestedTransactionIds(grainCase, tiers: null!);
        });

        Assert.Equal("tiers", exception.ParamName);
    }

    private static Task<List<string?>?[]> InvokeGetNestedTransactionIds(
        string grainCase,
        List<ITransactionAttributionGrain>[] tiers) =>
        grainCase switch
        {
            nameof(NoAttributionGrain) =>
                new NoAttributionGrain().GetNestedTransactionIds(0, tiers),
            nameof(SuppressAttributionGrain) =>
                new SuppressAttributionGrain().GetNestedTransactionIds(0, tiers),
            nameof(CreateOrJoinAttributionGrain) =>
                new CreateOrJoinAttributionGrain().GetNestedTransactionIds(0, tiers),
            nameof(CreateAttributionGrain) =>
                new CreateAttributionGrain().GetNestedTransactionIds(0, tiers),
            nameof(JoinAttributionGrain) =>
                new JoinAttributionGrain().GetNestedTransactionIds(0, tiers),
            nameof(SupportedAttributionGrain) =>
                new SupportedAttributionGrain().GetNestedTransactionIds(0, tiers),
            nameof(NotAllowedAttributionGrain) =>
                new NotAllowedAttributionGrain().GetNestedTransactionIds(0, tiers),
            _ => throw new ArgumentOutOfRangeException(nameof(grainCase), grainCase, null),
        };
}
