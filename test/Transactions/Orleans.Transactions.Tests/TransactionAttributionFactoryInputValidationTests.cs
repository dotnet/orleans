using Orleans.Transactions;
using Orleans.Transactions.TestKit;
using TestExtensions;
using Xunit;

namespace Orleans.Transactions.Tests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Transactions")]
[TestCategory("BVT"), TestCategory("Transactions")]
public class TransactionAttributionFactoryInputValidationTests
{
    [Fact]
    public void GetTransactionAttributionGrain_NullGrainFactory_ThrowsBeforeOptionDispatch()
    {
        var unsupportedOption = (TransactionOption)int.MaxValue;

        var exception = Assert.Throws<ArgumentNullException>(() =>
            TransactionAttributionGrainExtensions.GetTransactionAttributionGrain(
                grainFactory: null!,
                new Guid("B6589B9B-C606-481F-BF1A-7084332033B5"),
                unsupportedOption));

        Assert.Equal("grainFactory", exception.ParamName);
    }
}
