using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;

namespace DefaultCluster.Tests.General
{
    /// <summary>
    /// Tests for Orleans one-way method calls.
    /// One-way methods are grain methods that return immediately to the caller
    /// without waiting for the method execution to complete. This fire-and-forget
    /// pattern is useful for notifications, logging, and other scenarios where
    /// the caller doesn't need confirmation of completion or results.
    /// </summary>
    [TestCategory("BVT"), TestCategory("OneWay")]
    [TestSuite("BVT")]
    [TestProvider("None")]
    [TestArea("Runtime")]
    public class OneWayCallTests : HostedTestClusterEnsureDefaultStarted
    {
        public OneWayCallTests(DefaultClusterFixture fixture) : base(fixture) { }

        /// <summary>
        /// Tests that one-way methods return synchronously when called from a client.
        /// Verifies that:
        /// - The Task/ValueTask completes immediately without waiting for execution
        /// - The method still executes on the grain (observable via side effects)
        /// - Exceptions in one-way methods don't propagate to the caller
        /// </summary>
        [Fact]
        public async Task OneWayMethodsReturnSynchronously_ViaClient()
        {
            var grain = this.Client.GetGrain<IOneWayGrain>(Guid.NewGuid());
            const int expectedCount = 1;
            var countReached = grain.WaitForCount(expectedCount);

            var task = grain.Notify();
            Assert.True(task.Status == TaskStatus.RanToCompletion, "Task should be synchronously completed.");
            await WaitForCount(grain, countReached, nameof(OneWayMethodsReturnSynchronously_ViaClient), expectedCount);
            var count = await grain.GetCount();
            Assert.Equal(expectedCount, count);

            // This should not throw.
            task = grain.ThrowsOneWay();
            Assert.True(task.Status == TaskStatus.RanToCompletion, "Task should be synchronously completed.");
        }

        /// <summary>
        /// Tests that one-way methods return synchronously when called from another grain.
        /// Verifies that grain-to-grain one-way calls also complete immediately,
        /// allowing the calling grain to continue processing without blocking
        /// on the target grain's execution.
        /// </summary>
        [Fact]
        public async Task OneWayMethodReturnSynchronously_ViaGrain()
        {
            var grain = this.Client.GetGrain<IOneWayGrain>(Guid.NewGuid());
            var otherGrain = this.Client.GetGrain<IOneWayGrain>(Guid.NewGuid());
            const int expectedCount = 1;
            var countReached = otherGrain.WaitForCount(expectedCount);

            var completedSynchronously = await grain.NotifyOtherGrain(otherGrain);
            Assert.True(completedSynchronously, "Task should be synchronously completed.");
            await WaitForCount(otherGrain, countReached, nameof(OneWayMethodReturnSynchronously_ViaGrain), expectedCount);
            var count = await otherGrain.GetCount();
            Assert.Equal(expectedCount, count);
        }

        /// <summary>
        /// Tests one-way methods that return ValueTask instead of Task.
        /// Verifies that ValueTask-based one-way methods behave identically
        /// to Task-based ones, completing synchronously while still executing
        /// the method logic asynchronously on the target grain.
        /// </summary>
        [Fact]
        public async Task OneWayMethodsReturnSynchronously_ViaClient_ValueTask()
        {
            var grain = this.Client.GetGrain<IOneWayGrain>(Guid.NewGuid());
            const int expectedCount = 1;
            var countReached = grain.WaitForCount(expectedCount);

            var task = grain.NotifyValueTask();
            Assert.True(task.IsCompleted, "ValueTask should be synchronously completed.");
            await WaitForCount(grain, countReached, nameof(OneWayMethodsReturnSynchronously_ViaClient_ValueTask), expectedCount);
            var count = await grain.GetCount();
            Assert.Equal(expectedCount, count);

            // This should not throw.
            task = grain.ThrowsOneWayValueTask();
            Assert.True(task.IsCompleted, "Task should be synchronously completed.");
        }

        /// <summary>
        /// Tests ValueTask-based one-way methods called from another grain.
        /// Ensures that the ValueTask variant of one-way methods maintains
        /// the same fire-and-forget semantics in grain-to-grain communication.
        /// </summary>
        [Fact]
        public async Task OneWayMethodReturnSynchronously_ViaGrain_ValueTask()
        {
            var grain = this.Client.GetGrain<IOneWayGrain>(Guid.NewGuid());
            var otherGrain = this.Client.GetGrain<IOneWayGrain>(Guid.NewGuid());
            const int expectedCount = 1;
            var countReached = otherGrain.WaitForCount(expectedCount);

            var completedSynchronously = await grain.NotifyOtherGrainValueTask(otherGrain);
            Assert.True(completedSynchronously, "Task should be synchronously completed.");
            await WaitForCount(otherGrain, countReached, nameof(OneWayMethodReturnSynchronously_ViaGrain_ValueTask), expectedCount);
            var count = await otherGrain.GetCount();
            Assert.Equal(expectedCount, count);
        }

        private static async Task WaitForCount(IOneWayGrain grain, Task countReached, string scenario, int expectedCount)
        {
            try
            {
                await countReached.WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (TimeoutException exception)
            {
                var actualCount = await grain.GetCount();
                throw new TimeoutException(
                    $"Timed out waiting for one-way notification. Scenario: {scenario}. Expected count: {expectedCount}. Actual count: {actualCount}.",
                    exception);
            }
        }
    }
}