using Orleans.Internal;
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

            var observer = new SimpleGrainObserver();
            var task = grain.Notify(this.Client.CreateObjectReference<ISimpleGrainObserver>(observer));
            Assert.True(task.Status == TaskStatus.RanToCompletion, "Task should be synchronously completed.");
            await observer.ReceivedValue.WaitAsync(TimeSpan.FromSeconds(10));
            var count = await grain.GetCount();
            Assert.Equal(1, count);

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

            var observer = new SimpleGrainObserver();
            var observerReference = this.Client.CreateObjectReference<ISimpleGrainObserver>(observer);
            var completedSynchronously = await grain.NotifyOtherGrain(otherGrain, observerReference);
            Assert.True(completedSynchronously, "Task should be synchronously completed.");
            await observer.ReceivedValue.WaitAsync(TimeSpan.FromSeconds(10));
            var count = await otherGrain.GetCount();
            Assert.Equal(1, count);
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

            var observer = new SimpleGrainObserver();
            var task = grain.NotifyValueTask(this.Client.CreateObjectReference<ISimpleGrainObserver>(observer));
            Assert.True(task.IsCompleted, "ValueTask should be synchronously completed.");
            await observer.ReceivedValue.WaitAsync(TimeSpan.FromSeconds(10));
            var count = await grain.GetCount();
            Assert.Equal(1, count);

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

            var observer = new SimpleGrainObserver();
            var observerReference = this.Client.CreateObjectReference<ISimpleGrainObserver>(observer);
            var completedSynchronously = await grain.NotifyOtherGrainValueTask(otherGrain, observerReference);
            Assert.True(completedSynchronously, "Task should be synchronously completed.");
            await observer.ReceivedValue.WaitAsync(TimeSpan.FromSeconds(10));
            var count = await otherGrain.GetCount();
            Assert.Equal(1, count);
        }

        private class SimpleGrainObserver : ISimpleGrainObserver
        {
            private readonly TaskCompletionSource<int> completion = new TaskCompletionSource<int>();
            public Task ReceivedValue => this.completion.Task;
            public void StateChanged(int a, int b)
            {
                this.completion.SetResult(b);
            }
        }
    }
}