using Orleans.Transactions.TestKit;
using TestExtensions;
using Xunit;

namespace Orleans.Transactions.Tests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Transactions")]
[TestCategory("BVT"), TestCategory("Transactions")]
public class TransactionRecoveryTestsRunnerInputValidationTests
{
    [Fact]
    public void Constructor_NullTestCluster_ThrowsWithTestClusterParamNameBeforeBaseDereference()
    {
        var outputCallCount = 0;

        var exception = Assert.Throws<ArgumentNullException>(
            () => new TransactionRecoveryTestsRunner(
                testCluster: null!,
                testOutput: _ => outputCallCount++));

        Assert.Equal("testCluster", exception.ParamName);
        Assert.Equal(0, outputCallCount);
    }
}
