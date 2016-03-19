using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Orleans;
using Orleans.TestingHost;
using UnitTests.GrainInterfaces;
using Xunit;
using UnitTests.Tester;

namespace UnitTests.MembershipTests
{
    public class CancellationTokenPassingTests : HostedTestClusterPerTest
    {
        private double[] cancellationDelaysInMS = { 0, 0.1, 0.5, 1, 20, 500, 1000 };

        public override TestingSiloHost CreateSiloHost()
        {
            return new TestingSiloHost(new TestingSiloOptions
            {
                StartFreshOrleans = true,
                StartPrimary = true,
                StartSecondary = true,
                AdjustConfig = config =>
                {
                    config.Globals.DefaultPlacementStrategy = "ActivationCountBasedPlacement";
                }
            });
        }

        [Fact, TestCategory("Functional")]
        public async Task GrainTaskCancellation()
        {
            foreach (var delay in cancellationDelaysInMS.Select(TimeSpan.FromMilliseconds))
            {
                var grain = HostedCluster.GrainFactory.GetGrain<ILongRunningTaskGrain<bool>>(Guid.NewGuid());
                var tcs = new CancellationTokenSource();
                var wait = grain.LongWait(tcs.Token, TimeSpan.FromSeconds(10));
                await Task.Delay(delay);
                tcs.Cancel();
                await Xunit.Assert.ThrowsAsync<TaskCanceledException>(() => wait);
            }
        }

        [Fact, TestCategory("Functional")]
        public async Task PreCancelledTokenPassing()
        {
            var grain = HostedCluster.GrainFactory.GetGrain<ILongRunningTaskGrain<bool>>(Guid.NewGuid());
            var tcs = new CancellationTokenSource();
            tcs.Cancel();
            var wait = grain.LongWait(tcs.Token, TimeSpan.FromSeconds(10));
            await Assert.ThrowsAsync<TaskCanceledException>(() => wait);
        }

        [Fact, TestCategory("Functional")]
        public async Task InterSiloClientCancellationTokenPassing()
        {
            foreach (var delay in cancellationDelaysInMS.Select(TimeSpan.FromMilliseconds))
            {
                var grains = await GetGrains<bool>();
                var grain = grains.Item1;
                var target = grains.Item2;
                var tcs = new CancellationTokenSource();
                var wait = grain.CallOtherLongRunningTask(target, tcs.Token, TimeSpan.FromSeconds(10));
                await Task.Delay(delay);
                tcs.Cancel();
                await Assert.ThrowsAsync<TaskCanceledException>(() => wait);
            }
        }

        [Fact, TestCategory("Functional")]
        public async Task InterSiloGrainCancellation()
        {
            await GrainGrainCancellation(true);
        }

        [Fact, TestCategory("Functional")]
        public async Task InSiloGrainCancellation()
        {
            await GrainGrainCancellation(false);
        }

        private async Task GrainGrainCancellation(bool interSilo)
        {
            foreach (var delay in cancellationDelaysInMS)
            {
                var grains = await GetGrains<bool>(interSilo);
                var grain = grains.Item1;
                var target = grains.Item2;
                var wait = grain.CallOtherLongRunningTaskWithLocalToken(target, TimeSpan.FromSeconds(10),
                    TimeSpan.FromMilliseconds(delay));
                await Xunit.Assert.ThrowsAsync<TaskCanceledException>(() => wait);
            }
        }

        private async Task<Tuple<ILongRunningTaskGrain<T1>, ILongRunningTaskGrain<T1>>> GetGrains<T1>(bool placeOnDifferentSilos = true)
        {
            var grain = HostedCluster.GrainFactory.GetGrain<ILongRunningTaskGrain<T1>>(Guid.NewGuid());
            var instanceId = await grain.GetRuntimeInstanceId();
            var target = HostedCluster.GrainFactory.GetGrain<ILongRunningTaskGrain<T1>>(Guid.NewGuid());
            var targetInstanceId = await target.GetRuntimeInstanceId();
            if (placeOnDifferentSilos)
            {
                while (instanceId.Equals(targetInstanceId))
                {
                    target = HostedCluster.GrainFactory.GetGrain<ILongRunningTaskGrain<T1>>(Guid.NewGuid());
                    targetInstanceId = await target.GetRuntimeInstanceId();
                }
            }
            else
            {
                while (!instanceId.Equals(targetInstanceId))
                {
                    target = HostedCluster.GrainFactory.GetGrain<ILongRunningTaskGrain<T1>>(Guid.NewGuid());
                    targetInstanceId = await target.GetRuntimeInstanceId();
                }
            }

            return new Tuple<ILongRunningTaskGrain<T1>, ILongRunningTaskGrain<T1>>(grain, target);
        }
    }
}
