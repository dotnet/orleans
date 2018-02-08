using System;
using System.Threading.Tasks;
using Orleans;
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
            var task = grain.Notify(await this.Client.CreateObjectReference<ISimpleGrainObserver>(observer));
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
            var observerReference = await this.Client.CreateObjectReference<ISimpleGrainObserver>(observer);
            var completedSynchronously = await grain.NotifyOtherGrain(otherGrain, observerReference);
            Assert.True(completedSynchronously, "Task should be synchronously completed.");
            await observer.ReceivedValue.WithTimeout(TimeSpan.FromSeconds(10));
            var count = await otherGrain.GetCount();
            Assert.Equal(1, count);
        }

        [Fact]
        public async Task MethodsInvokedThroughOneWayExtensionReturnSynchronously()
        {
            var grain = this.Client.GetGrain<ICanBeOneWayGrain>(Guid.NewGuid());

            var observer = new SimpleGrainObserver();
            var observerRef = await Client.CreateObjectReference<ISimpleGrainObserver>(observer);
            grain.InvokeOneWay(g =>
            {
                Assert.False(object.ReferenceEquals(g, grain), "One way call should be executed on copy of grain reference");
                Assert.Equal(g, grain);
                return g.Notify(observerRef);
            });

            await observer.ReceivedValue.WithTimeout(TimeSpan.FromSeconds(10));
            var count = await grain.GetCount();
            Assert.Equal(1, count);

            // This should not throw.
            grain.InvokeOneWay(g => g.Throws());
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