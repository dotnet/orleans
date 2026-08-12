using Orleans.Transactions.TestKit;
using Xunit;

namespace Orleans.Transactions.Tests;

public class ConsistencyTransactionTestRunnerTests
{
    [Theory]
    [InlineData(2, false, false)]
    [InlineData(3, false, true)]
    [InlineData(2, true, true)]
    public void GenericTimeoutToleranceMatchesWorkloadRisk(
        int scale,
        bool storageErrorInjectionActive,
        bool expected)
    {
        var actual = ConsistencyTransactionTestRunner.ShouldTolerateGenericTimeouts(
            scale,
            storageErrorInjectionActive);

        Assert.Equal(expected, actual);
    }
}
