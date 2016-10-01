using System;
using System.Linq;
using System.Threading.Tasks;
using Orleans;
using Orleans.TestingHost;
using Tester;
using UnitTests.GrainInterfaces;
using UnitTests.Tester;
using Xunit;

namespace UnitTests.CancellationTests
{
    public class GrainCancellationTokenTests : OrleansTestingBase, IClassFixture<GrainCancellationTokenTests.Fixture>
    {
        private class Fixture : BaseTestClusterFixture
        {
            protected override TestCluster CreateTestCluster()
            {
                return new TestCluster(new TestClusterOptions(2));
            }
        }

        [Theory, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Cancellation")]
        // [InlineData(0)] disabled until resolve of the https://github.com/dotnet/orleans/issues/1891
        // [InlineData(10)]
        [InlineData(300)]
        public async Task GrainTaskCancellation(int delay)
        {
            var grain = GrainFactory.GetGrain<ILongRunningTaskGrain<bool>>(Guid.NewGuid());
            var tcs = new GrainCancellationTokenSource();
            var grainTask = grain.LongWait(tcs.Token, TimeSpan.FromSeconds(10));
            await Task.Delay(TimeSpan.FromMilliseconds(delay));
            await tcs.Cancel();
            await Assert.ThrowsAsync<TaskCanceledException>(() => grainTask);
        }

        [Theory, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Cancellation")]
        // [InlineData(0)]
        // [InlineData(10)]
        [InlineData(300)]
        public async Task MultipleGrainsTaskCancellation(int delay)
        {
            var tcs = new GrainCancellationTokenSource();
            var grainTasks = Enumerable.Range(0, 5)
                .Select(i => GrainFactory.GetGrain<ILongRunningTaskGrain<bool>>(Guid.NewGuid())
                            .LongWait(tcs.Token, TimeSpan.FromSeconds(10)))
                            .Select(task => Assert.ThrowsAsync<TaskCanceledException>(() => task)).ToList();
            await Task.Delay(TimeSpan.FromMilliseconds(delay));
            await tcs.Cancel();
            await Task.WhenAll(grainTasks);
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Cancellation")]
        public async Task TokenPassingWithoutCancellation_NoExceptionShouldBeThrown()
        {
            var grain = GrainFactory.GetGrain<ILongRunningTaskGrain<bool>>(Guid.NewGuid());
            var tcs = new GrainCancellationTokenSource();
            try
            {
                await grain.LongWait(tcs.Token, TimeSpan.FromMilliseconds(1));
            }
            catch (Exception ex)
            {
                Assert.True(false, "Expected no exception, but got: " + ex.Message);
            }
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Cancellation")]
        public async Task PreCancelledTokenPassing()
        {
            var grain = GrainFactory.GetGrain<ILongRunningTaskGrain<bool>>(Guid.NewGuid());
            var tcs = new GrainCancellationTokenSource();
            await tcs.Cancel();
            var grainTask = grain.LongWait(tcs.Token, TimeSpan.FromSeconds(10));
            await Assert.ThrowsAsync<TaskCanceledException>(() => grainTask);
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Cancellation")]
        public async Task CancellationTokenCallbacksExecutionContext()
        {
            var grain = GrainFactory.GetGrain<ILongRunningTaskGrain<bool>>(Guid.NewGuid());
            var tcs = new GrainCancellationTokenSource();
            var grainTask = grain.CancellationTokenCallbackResolve(tcs.Token);
            await Task.Delay(TimeSpan.FromMilliseconds(100));
            await tcs.Cancel();
            var result = await grainTask;
            Assert.Equal(true, result);
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Cancellation")]
        public async Task CancellationTokenCallbacksTaskSchedulerContext()
        {
            var grains = await GetGrains<bool>(false);

            var tcs = new GrainCancellationTokenSource();
            var grainTask = grains.Item1.CallOtherCancellationTokenCallbackResolve(grains.Item2);
            await tcs.Cancel();
            var result = await grainTask;
            Assert.Equal(true, result);
        }

        [Fact, TestCategory("Cancellation")]
        public async Task CancellationTokenCallbacksThrow_ExceptionShouldBePropagated()
        {
            var grain = GrainFactory.GetGrain<ILongRunningTaskGrain<bool>>(Guid.NewGuid());
            var tcs = new GrainCancellationTokenSource();
            var grainTask = grain.CancellationTokenCallbackThrow(tcs.Token);
            await Task.Delay(TimeSpan.FromMilliseconds(100));
            try
            {
                await tcs.Cancel();
            }
            catch (AggregateException ex)
            {
                Assert.True(ex.InnerException is InvalidOperationException, "Exception thrown has wrong type");
                return;
            }

            Assert.True(false, "No exception was thrown");
        }

        [Theory, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Cancellation")]
        // [InlineData(0)]
        // [InlineData(10)]
        [InlineData(300)]
        public async Task InSiloGrainCancellation(int delay)
        {
            await GrainGrainCancellation(false, delay);
        }

        [Theory, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Cancellation")]
        // [InlineData(0)]
        // [InlineData(10)]
        [InlineData(300)]
        public async Task InterSiloGrainCancellation(int delay)
        {
            await GrainGrainCancellation(true, delay);
        }

        [Theory, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Cancellation")]
        // [InlineData(0)]
        // [InlineData(10)]
        [InlineData(300)]
        public async Task InterSiloClientCancellationTokenPassing(int delay)
        {
            await ClientGrainGrainTokenPassing(delay, true);
        }

        [Theory, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Cancellation")]
        // [InlineData(0)]
        // [InlineData(10)]
        [InlineData(300)]
        public async Task InSiloClientCancellationTokenPassing(int delay)
        {
            await ClientGrainGrainTokenPassing(delay, false);
        }

        private async Task ClientGrainGrainTokenPassing(int delay, bool interSilo)
        {
            var grains = await GetGrains<bool>(interSilo);
            var grain = grains.Item1;
            var target = grains.Item2;
            var tcs = new GrainCancellationTokenSource();
            var grainTask = grain.CallOtherLongRunningTask(target, tcs.Token, TimeSpan.FromSeconds(10));
            await Task.Delay(TimeSpan.FromMilliseconds(delay));
            await tcs.Cancel();
            await Assert.ThrowsAsync<TaskCanceledException>(() => grainTask);
        }

        private async Task GrainGrainCancellation(bool interSilo, int delay)
        {
            var grains = await GetGrains<bool>(interSilo);
            var grain = grains.Item1;
            var target = grains.Item2;
            var grainTask = grain.CallOtherLongRunningTaskWithLocalToken(target, TimeSpan.FromSeconds(10),
                TimeSpan.FromMilliseconds(delay));
            await Assert.ThrowsAsync<TaskCanceledException>(() => grainTask);
        }

        private async Task<Tuple<ILongRunningTaskGrain<T1>, ILongRunningTaskGrain<T1>>> GetGrains<T1>(bool placeOnDifferentSilos = true)
        {
            var grain = GrainFactory.GetGrain<ILongRunningTaskGrain<T1>>(Guid.NewGuid());
            var instanceId = await grain.GetRuntimeInstanceId();
            var target = GrainFactory.GetGrain<ILongRunningTaskGrain<T1>>(Guid.NewGuid());
            var targetInstanceId = await target.GetRuntimeInstanceId();
            var retriesCount = 0;
            var retriesLimit = 10;
            
            while ((placeOnDifferentSilos && instanceId.Equals(targetInstanceId))
                || (!placeOnDifferentSilos && !instanceId.Equals(targetInstanceId)))
            {
                if (retriesCount >= retriesLimit) throw new Exception("Could not make requested grains placement");
                target = GrainClient.GrainFactory.GetGrain<ILongRunningTaskGrain<T1>>(Guid.NewGuid());
                targetInstanceId = await target.GetRuntimeInstanceId();
                retriesCount++;
            }

            return new Tuple<ILongRunningTaskGrain<T1>, ILongRunningTaskGrain<T1>>(grain, target);
        }
    }
}
