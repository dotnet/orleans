using Microsoft.Extensions.Logging;
using Orleans.Runtime.Placement;
using Orleans.TestingHost;
using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;

namespace UnitTests.CancellationTests
{
    /// <summary>
    /// Tests for GrainCancellationToken functionality including task cancellation and token callbacks.
    /// </summary>
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
            using var cts = new GrainCancellationTokenSource();
            var callId = Guid.NewGuid();
            var grainTask = grain.LongWaitGrainCancellation(cts.Token, TimeSpan.FromSeconds(10), callId);
            await Task.Delay(TimeSpan.FromMilliseconds(delay));
            await cts.Cancel();
            await Assert.ThrowsAsync<TaskCanceledException>(() => grainTask);
            await WaitForCallCancellation(grain, callId);
        }

        [Theory, TestCategory("BVT"), TestCategory("Cancellation")]
        [InlineData(0)]
        [InlineData(10)]
        [InlineData(300)]
        public async Task MultipleGrainsTaskCancellation(int delay)
        {
            using var cts = new GrainCancellationTokenSource();
            var callId = Guid.NewGuid();
            var grains = Enumerable.Range(0, 5).Select(_ => this.fixture.GrainFactory.GetGrain<ILongRunningTaskGrain<bool>>(Guid.NewGuid())).ToList();
            var grainTasks = grains
                .Select(grain => Assert.ThrowsAsync<TaskCanceledException>(() => grain
                            .LongWaitGrainCancellationInterleaving(cts.Token, TimeSpan.FromSeconds(10), callId))).ToList();
            await Task.Delay(TimeSpan.FromMilliseconds(delay));
            await cts.Cancel();
            await Task.WhenAll(grainTasks);
            if (delay > 0)
            {
                foreach (var grain in grains)
                {
                    await WaitForCallCancellation(grain, callId);
                }
            }
        }

        [Theory, TestCategory("BVT"), TestCategory("Cancellation")]
        [InlineData(0)]
        [InlineData(10)]
        [InlineData(300)]
        public async Task GrainTaskMultipleCancellations(int delay)
        {
            var grain = this.fixture.GrainFactory.GetGrain<ILongRunningTaskGrain<bool>>(Guid.NewGuid());
            var callIds = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).ToArray();
            var grainTasks = callIds
                .Select(async callId =>
                {
                    using var cts = new GrainCancellationTokenSource();
                    var task = grain.LongWaitGrainCancellationInterleaving(cts.Token, TimeSpan.FromSeconds(10), callId);
                    await Task.WhenAny(task, Task.Delay(TimeSpan.FromMilliseconds(delay)));
                    await cts.Cancel();
                    try
                    {
                        await task;
                        Assert.Fail("Expected TaskCancelledException, but message completed");
                    }
                    catch (TaskCanceledException) { }
                })
                .ToList();
            await Task.WhenAll(grainTasks);
            if (delay > 0)
            {
                await WaitForCallCancellation(grain, callIds);
            }
        }

        [Fact, TestCategory("BVT"), TestCategory("Cancellation")]
        public async Task TokenPassingWithoutCancellation_NoExceptionShouldBeThrown()
        {
            var grain = this.fixture.GrainFactory.GetGrain<ILongRunningTaskGrain<bool>>(Guid.NewGuid());
            using var cts = new GrainCancellationTokenSource();
            try
            {
                await grain.LongWaitGrainCancellation(cts.Token, TimeSpan.FromMilliseconds(1), Guid.Empty);
            }
            catch (Exception ex)
            {
                Assert.Fail("Expected no exception, but got: " + ex.Message);
            }
        }

        [Fact, TestCategory("BVT"), TestCategory("Cancellation")]
        public async Task PreCancelledTokenPassing()
        {
            var grain = this.fixture.GrainFactory.GetGrain<ILongRunningTaskGrain<bool>>(Guid.NewGuid());
            using var cts = new GrainCancellationTokenSource();
            await cts.Cancel();
            var callId = Guid.NewGuid();
            var grainTask = grain.LongWaitGrainCancellation(cts.Token, TimeSpan.FromSeconds(10), callId);
            await Assert.ThrowsAsync<TaskCanceledException>(() => grainTask);
            await WaitForCallCancellation(grain, callId);
        }

        [Fact, TestCategory("BVT"), TestCategory("Cancellation")]
        public async Task CancellationTokenCallbacksExecutionContext()
        {
            var grain = this.fixture.GrainFactory.GetGrain<ILongRunningTaskGrain<bool>>(Guid.NewGuid());
            using var cts = new GrainCancellationTokenSource();
            var callId = Guid.NewGuid();
            var grainTask = grain.GrainCancellationTokenCallbackResolve(cts.Token, callId);
            await Task.Delay(TimeSpan.FromMilliseconds(100));
            await cts.Cancel();
            var result = await grainTask;
            Assert.True(result);
            await WaitForCallCancellation(grain, callId);
        }

        [Fact, TestCategory("BVT"), TestCategory("Cancellation")]
        public async Task CancellationTokenCallbacksTaskSchedulerContext()
        {
            var grains = await GetGrains<bool>(false);

            using var cts = new GrainCancellationTokenSource();
            var callId = Guid.NewGuid();
            var grainTask = grains.Item1.CallOtherGrainCancellationTokenCallbackResolve(grains.Item2, callId);
            await cts.Cancel();
            var result = await grainTask;
            Assert.True(result);
            await WaitForCallCancellation(grains.Item2, callId);
        }

        [Fact, TestCategory("Cancellation")]
        public async Task CancellationTokenCallbacksThrow_ExceptionShouldBePropagated()
        {
            var grain = this.fixture.GrainFactory.GetGrain<ILongRunningTaskGrain<bool>>(Guid.NewGuid());
            using var cts = new GrainCancellationTokenSource();
            var callId = Guid.NewGuid();
            grain.GrainCancellationTokenCallbackThrow(cts.Token, callId).Ignore();
            await Task.Delay(TimeSpan.FromMilliseconds(100));
            try
            {
                await cts.Cancel();
            }
            catch (AggregateException ex)
            {
                Assert.True(ex.InnerException is InvalidOperationException, "Exception thrown has wrong type");
                return;
            }

            Assert.Fail("No exception was thrown");
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

        [Theory, TestCategory("BVT"), TestCategory("Cancellation")]
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
            var cts = new GrainCancellationTokenSource();
            var callId = Guid.NewGuid();
            var grainTask = grain.CallOtherLongRunningTaskGrainCancellation(target, cts.Token, TimeSpan.FromSeconds(10), callId);
            await Task.Delay(TimeSpan.FromMilliseconds(delay));
            await cts.Cancel();
            await Assert.ThrowsAsync<TaskCanceledException>(() => grainTask);
            if (delay > 0)
            {
                await WaitForCallCancellation(grains.Item2, callId);
            }
        }

        private async Task GrainGrainCancellation(bool interSilo, int delay)
        {
            var grains = await GetGrains<bool>(interSilo);
            var grain = grains.Item1;
            var target = grains.Item2;
            var callId = Guid.NewGuid();
            var grainTask = grain.CallOtherLongRunningTaskWithLocalGrainCancellationToken(target, TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(delay), callId);
            await Assert.ThrowsAsync<TaskCanceledException>(() => grainTask);
            if (delay > 0)
            {
                await WaitForCallCancellation(grains.Item2, callId);
            }
        }

    private async Task<Tuple<ILongRunningTaskGrain<T1>, ILongRunningTaskGrain<T1>>> GetGrains<T1>(bool placeOnDifferentSilos = true)
    {
        var attemptNumber = 0;
        var attemptLimit = 50;
        ILongRunningTaskGrain<T1> grain, target;
        string instanceId, targetInstanceId;
        do
        {
            if (attemptNumber > 0)
            {
                if (attemptNumber >= attemptLimit)
                {
                    throw new Exception("Could not make requested grains placement");
                }

                await Task.Delay(500);
            }

            ++attemptNumber;
            var firstSilo = fixture.HostedCluster.Silos.First().SiloAddress;
            RequestContext.Set(IPlacementDirector.PlacementHintKey, firstSilo);
            grain = fixture.GrainFactory.GetGrain<ILongRunningTaskGrain<T1>>(Guid.NewGuid());
            instanceId = await grain.GetRuntimeInstanceId();

            if (placeOnDifferentSilos)
            {
                var secondSilo = fixture.HostedCluster.Silos.Skip(1).First().SiloAddress;
                RequestContext.Set(IPlacementDirector.PlacementHintKey, secondSilo);
            }

            target = fixture.GrainFactory.GetGrain<ILongRunningTaskGrain<T1>>(Guid.NewGuid());
            targetInstanceId = await target.GetRuntimeInstanceId();
            RequestContext.Clear();
        } while (placeOnDifferentSilos && instanceId.Equals(targetInstanceId) || !placeOnDifferentSilos && !instanceId.Equals(targetInstanceId));

        return new Tuple<ILongRunningTaskGrain<T1>, ILongRunningTaskGrain<T1>>(grain, target);
    }

        private async Task WaitForCallCancellation<T>(ILongRunningTaskGrain<T> grain, Guid callId)
        {
            using var cts = new CancellationTokenSource();
            cts.CancelAfter(TimeSpan.FromSeconds(30));
            await foreach (var (cancelledCallId, error) in grain.WatchCancellations(cts.Token))
            {
                if (cancelledCallId == callId)
                {
                    if (error is not null)
                    {
                        throw new Exception("Expected no error, but found an error", error);
                    }

                    return;
                }
            }

            Assert.Fail("Did not encounter the expected call id");
        }

        private async Task WaitForCallCancellation<T>(ILongRunningTaskGrain<T> grain, Guid[] callIds)
        {
            using var cts = new CancellationTokenSource();
            var targetIds = new HashSet<Guid>(callIds);
            cts.CancelAfter(TimeSpan.FromSeconds(300));
            await foreach (var (cancelledCallId, error) in grain.WatchCancellations(cts.Token))
            {
                if (targetIds.Remove(cancelledCallId))
                {
                    if (error is not null)
                    {
                        throw new Exception("Expected no error, but found an error", error);
                    }

                    if (targetIds.Count == 0)
                    {
                        return;
                    }
                }
            }

            Assert.Fail("Did not encounter the expected call id");
        }
    }
}
