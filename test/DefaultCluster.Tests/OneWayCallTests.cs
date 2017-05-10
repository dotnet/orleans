using System;
using System.Threading.Tasks;
using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;

namespace DefaultCluster.Tests.General
{
    public class OneWayCallTests : HostedTestClusterEnsureDefaultStarted
    {
        public OneWayCallTests(DefaultClusterFixture fixture) : base(fixture) { }

        [Fact]
        public async Task OneWayMethodsReturnSynchronously()
        {
            var grain = this.Client.GetGrain<IOneWayGrain>(Guid.NewGuid());

            var observer = new SimpleGrainObserver();
            var task = grain.Notify(await this.Client.CreateObjectReference<ISimpleGrainObserver>(observer));
            Assert.True(task.Status == TaskStatus.RanToCompletion, "Task should be synchronously completed.");
            await observer.ReceivedValue;
            var count = await grain.GetCount();
            Assert.Equal(1, count);

            // This should not throw.
            task = grain.ThrowsOneWay();
            Assert.True(task.Status == TaskStatus.RanToCompletion, "Task should be synchronously completed.");
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