using Orleans.Transactions.TestKit.xUnit;
using Xunit;
using Xunit.Abstractions;

namespace Orleans.Transactions.DynamoDB.Tests
{
    /// <summary>
    /// Tests for Transfer of Coordination (TOC) golden path scenarios with DynamoDB.
    /// </summary>
    [TestCategory("DynamoDB"), TestCategory("Transactions"), TestCategory("Functional")]
    public class TocGoldenPathTests : TocGoldenPathTestRunnerxUnit, IClassFixture<TestFixture>
    {
        public TocGoldenPathTests(TestFixture fixture, ITestOutputHelper output)
            : base(fixture.GrainFactory, output)
        {
            fixture.EnsurePreconditionsMet();
        }
    }
}
