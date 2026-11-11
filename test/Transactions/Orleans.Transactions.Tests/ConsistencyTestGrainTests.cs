using Orleans.Transactions.TestKit.Consistency;
using TestExtensions;
using Xunit;

namespace Orleans.Transactions.Tests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Transactions")]
[TestCategory("BVT"), TestCategory("Transactions")]
public class ConsistencyTestGrainTests(MemoryTransactionsFixture fixture) : IClassFixture<MemoryTransactionsFixture>
{
    private static long _nextGrainKey;

    [Theory]
    [InlineData(false, true, 1)]
    [InlineData(true, true, 1)]
    [InlineData(false, false, 6)]
    [InlineData(true, false, 6)]
    public async Task Run_CollectsCompletedRecursiveCalls(
        bool avoidDeadlocks,
        bool deadlineExpired,
        int expectedWrites)
    {
        var key = Interlocked.Increment(ref _nextGrainKey) * ConsistencyTestOptions.MaxGrains;
        var options = new ConsistencyTestOptions
        {
            // This seed selects recursion, then a write in the first nested call and six writes in all ten calls.
            RandomSeed = 213,
            NumGrains = 1,
            MaxDepth = 1,
            AvoidDeadlocks = avoidDeadlocks,
            ReadWrite = ReadWriteDetermination.PerAccess,
            GrainOffset = key,
        };
        var grain = fixture.GrainFactory.GetGrain<IConsistencyTestGrain>(key);

        var observations = await grain.Run(
            options,
            depth: 0,
            stack: "(0)",
            max: 1,
            stopAfter: deadlineExpired ? DateTime.MinValue : DateTime.MaxValue)
            .WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(Enumerable.Range(0, expectedWrites + 1), observations.Select(observation => observation.SeqNo).Order());
        var transactionId = Assert.Single(observations.Select(observation => observation.ExecutingTx).Distinct());
        Assert.True(Guid.TryParse(transactionId, out _));
        Assert.All(observations, observation =>
        {
            Assert.Equal(0, observation.Grain);
            Assert.Equal(observation.SeqNo == 0 ? ConsistencyTestHarness.InitialTx : transactionId, observation.WriterTx);
        });
    }
}
