using Orleans.Internal;
using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;

namespace DefaultCluster.Tests.General
{
    [TestCategory("BVT"), TestCategory("OneWay")]
    public class OneWayCallTests : HostedTestClusterEnsureDefaultStarted
    {
        public OneWayCallTests(DefaultClusterFixture fixture) : base(fixture) { }

        [Fact]
        public async Task OneWayMethodsReturnSynchronously_ViaClient()
        {
            var grain = this.Client.GetGrain<IOneWayGrain>(Guid.NewGuid());

            var observer = new SimpleGrainObserver();
            var task = grain.Notify(this.Client.CreateObjectReference<ISimpleGrainObserver>(observer));
            Assert.True(task.Status == TaskStatus.RanToCompletion, "Task should be synchronously completed.");
            await observer.ReceivedValue.WithTimeout(TimeSpan.FromSeconds(10));
            var count = await grain.GetCount();
            Assert.Equal(1, count);

            // This should not throw.
            task = grain.ThrowsOneWay();
            Assert.True(task.Status == TaskStatus.RanToCompletion, "Task should be synchronously completed.");
        }

        [Fact]
        public async Task OneWayMethodReturnSynchronously_ViaGrain()
        {
            var grain = this.Client.GetGrain<IOneWayGrain>(Guid.NewGuid());
            var otherGrain = this.Client.GetGrain<IOneWayGrain>(Guid.NewGuid());

            var observer = new SimpleGrainObserver();
            var observerReference = this.Client.CreateObjectReference<ISimpleGrainObserver>(observer);
            var completedSynchronously = await grain.NotifyOtherGrain(otherGrain, observerReference);
            Assert.True(completedSynchronously, "Task should be synchronously completed.");
            await observer.ReceivedValue.WithTimeout(TimeSpan.FromSeconds(10));
            var count = await otherGrain.GetCount();
            Assert.Equal(1, count);
        }

        [Fact]
        public async Task OneWayMethodsReturnSynchronously_ViaClient_ValueTask()
        {
            var grain = this.Client.GetGrain<IOneWayGrain>(Guid.NewGuid());

            var observer = new SimpleGrainObserver();
            var task = grain.NotifyValueTask(this.Client.CreateObjectReference<ISimpleGrainObserver>(observer));
            Assert.True(task.IsCompleted, "ValueTask should be synchronously completed.");
            await observer.ReceivedValue.WithTimeout(TimeSpan.FromSeconds(10));
            var count = await grain.GetCount();
            Assert.Equal(1, count);

            // This should not throw.
            task = grain.ThrowsOneWayValueTask();
            Assert.True(task.IsCompleted, "Task should be synchronously completed.");
        }

        [Fact]
        public async Task OneWayMethodReturnSynchronously_ViaGrain_ValueTask()
        {
            var grain = this.Client.GetGrain<IOneWayGrain>(Guid.NewGuid());
            var otherGrain = this.Client.GetGrain<IOneWayGrain>(Guid.NewGuid());

            var observer = new SimpleGrainObserver();
            var observerReference = this.Client.CreateObjectReference<ISimpleGrainObserver>(observer);
            var completedSynchronously = await grain.NotifyOtherGrainValueTask(otherGrain, observerReference);
            Assert.True(completedSynchronously, "Task should be synchronously completed.");
            await observer.ReceivedValue.WithTimeout(TimeSpan.FromSeconds(10));
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