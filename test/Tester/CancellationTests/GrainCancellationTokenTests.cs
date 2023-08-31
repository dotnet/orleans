using Microsoft.Extensions.Logging;
using Orleans.TestingHost;
using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;

namespace UnitTests.CancellationTests
{
    public class GrainCancellationTokenTests : OrleansTestingBase, IClassFixture<GrainCancellationTokenTests.Fixture>
    {
        private readonly Fixture fixture;

        public class Fixture : BaseTestClusterFixture
        {
            protected override void ConfigureTestCluster(TestClusterBuilder builder)
            {
                base.ConfigureTestCluster(builder);
                builder.AddSiloBuilderConfigurator<SiloConfig>();
            }

            private class SiloConfig : ISiloConfigurator
            {
                public void Configure(ISiloBuilder siloBuilder)
                {
                    siloBuilder.ConfigureLogging(logging => logging.AddDebug());
                }
            }
        }

        public GrainCancellationTokenTests(Fixture fixture)
        {
            this.fixture = fixture;
        }

        [Theory, TestCategory("BVT"), TestCategory("Cancellation")]
        [InlineData(0)]
        [InlineData(10)]
        [InlineData(300)]
        public async Task GrainTaskCancellation(int delay)
        {
            var grain = this.fixture.GrainFactory.GetGrain<ILongRunningTaskGrain<bool>>(Guid.NewGuid());
            var tcs = new GrainCancellationTokenSource();
            var grainTask = grain.LongWait(tcs.Token, TimeSpan.FromSeconds(10));
            await Task.Delay(TimeSpan.FromMilliseconds(delay));
            await tcs.Cancel();
            await Assert.ThrowsAsync<TaskCanceledException>(() => grainTask);
        }

        [Theory, TestCategory("BVT"), TestCategory("Cancellation")]
        [InlineData(0)]
        [InlineData(10)]
        [InlineData(300)]
        public async Task MultipleGrainsTaskCancellation(int delay)
        {
            var tcs = new GrainCancellationTokenSource();
            var grainTasks = Enumerable.Range(0, 5)
                .Select(i => this.fixture.GrainFactory.GetGrain<ILongRunningTaskGrain<bool>>(Guid.NewGuid())
                            .LongWait(tcs.Token, TimeSpan.FromSeconds(10)))
                            .Select(task => Assert.ThrowsAsync<TaskCanceledException>(() => task)).ToList();
            await Task.Delay(TimeSpan.FromMilliseconds(delay));
            await tcs.Cancel();
            await Task.WhenAll(grainTasks);
        }

        [Theory, TestCategory("BVT"), TestCategory("Cancellation")]
        [InlineData(0)]
        [InlineData(10)]
        [InlineData(300)]
        public async Task GrainTaskMultipleCancellations(int delay)
        {
            var grain = this.fixture.GrainFactory.GetGrain<ILongRunningTaskGrain<bool>>(Guid.NewGuid());
            var grainTasks = Enumerable.Range(0, 5)
                .Select(async i =>
                {
                    var tcs = new GrainCancellationTokenSource();
                    var task = grain.LongWait(tcs.Token, TimeSpan.FromSeconds(10));
                    await Task.WhenAny(task, Task.Delay(TimeSpan.FromMilliseconds(delay)));
                    await tcs.Cancel();
                    try
                    {
                        await task;
                        Assert.True(false, "Expected TaskCancelledException, but message completed");
                    }
                    catch (TaskCanceledException) { }
                })
                .ToList();
            await Task.WhenAll(grainTasks);
        }

        [Fact, TestCategory("BVT"), TestCategory("Cancellation")]
        public async Task TokenPassingWithoutCancellation_NoExceptionShouldBeThrown()
        {
            var grain = this.fixture.GrainFactory.GetGrain<ILongRunningTaskGrain<bool>>(Guid.NewGuid());
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

        [Fact, TestCategory("BVT"), TestCategory("Cancellation")]
        public async Task PreCancelledTokenPassing()
        {
            var grain = this.fixture.GrainFactory.GetGrain<ILongRunningTaskGrain<bool>>(Guid.NewGuid());
            var tcs = new GrainCancellationTokenSource();
            await tcs.Cancel();
            var grainTask = grain.LongWait(tcs.Token, TimeSpan.FromSeconds(10));
            await Assert.ThrowsAsync<TaskCanceledException>(() => grainTask);
        }

        [Fact, TestCategory("BVT"), TestCategory("Cancellation")]
        public async Task CancellationTokenCallbacksExecutionContext()
        {
            var grain = this.fixture.GrainFactory.GetGrain<ILongRunningTaskGrain<bool>>(Guid.NewGuid());
            var tcs = new GrainCancellationTokenSource();
            var grainTask = grain.CancellationTokenCallbackResolve(tcs.Token);
            await Task.Delay(TimeSpan.FromMilliseconds(100));
            await tcs.Cancel();
            var result = await grainTask;
            Assert.True(result);
        }

        [Fact, TestCategory("BVT"), TestCategory("Cancellation")]
        public async Task CancellationTokenCallbacksTaskSchedulerContext()
        {
            var grains = await GetGrains<bool>(false);

            var tcs = new GrainCancellationTokenSource();
            var grainTask = grains.Item1.CallOtherCancellationTokenCallbackResolve(grains.Item2);
            await tcs.Cancel();
            var result = await grainTask;
            Assert.True(result);
        }

        [Fact, TestCategory("Cancellation")]
        public async Task CancellationTokenCallbacksThrow_ExceptionShouldBePropagated()
        {
            var grain = this.fixture.GrainFactory.GetGrain<ILongRunningTaskGrain<bool>>(Guid.NewGuid());
            var tcs = new GrainCancellationTokenSource();
            _ = grain.CancellationTokenCallbackThrow(tcs.Token);
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

        [Theory, TestCategory("BVT"), TestCategory("Cancellation")]
        [InlineData(0)]
        [InlineData(10)]
        [InlineData(300)]
        public async Task InSiloGrainCancellation(int delay)
        {
            await GrainGrainCancellation(false, delay);
        }

        [Theory, TestCategory("BVT"), TestCategory("Cancellation")]
        [InlineData(0)]
        [InlineData(10)]
        [InlineData(300)]
        public async Task InterSiloGrainCancellation(int delay)
        {
            await GrainGrainCancellation(true, delay);
        }

        [SkippableTheory(Skip="https://github.com/dotnet/orleans/issues/5654"), TestCategory("BVT"), TestCategory("Cancellation")]
        [InlineData(0)]
        [InlineData(10)]
        [InlineData(300)]
        public async Task InterSiloClientCancellationTokenPassing(int delay)
        {
            await ClientGrainGrainTokenPassing(delay, true);
        }

        [Theory, TestCategory("BVT"), TestCategory("Cancellation")]
        [InlineData(0)]
        [InlineData(10)]
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
            var grain = this.fixture.GrainFactory.GetGrain<ILongRunningTaskGrain<T1>>(Guid.NewGuid());
            var instanceId = await grain.GetRuntimeInstanceId();
            var target = this.fixture.GrainFactory.GetGrain<ILongRunningTaskGrain<T1>>(Guid.NewGuid());
            var targetInstanceId = await target.GetRuntimeInstanceId();
            var retriesCount = 0;
            var retriesLimit = 10;
            
            while ((placeOnDifferentSilos && instanceId.Equals(targetInstanceId))
                || (!placeOnDifferentSilos && !instanceId.Equals(targetInstanceId)))
            {
                if (retriesCount >= retriesLimit) throw new Exception("Could not make requested grains placement");
                target = this.fixture.GrainFactory.GetGrain<ILongRunningTaskGrain<T1>>(Guid.NewGuid());
                targetInstanceId = await target.GetRuntimeInstanceId();
                retriesCount++;
            }

            return new Tuple<ILongRunningTaskGrain<T1>, ILongRunningTaskGrain<T1>>(grain, target);
        }
    }
}
