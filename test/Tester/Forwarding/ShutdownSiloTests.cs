using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Runtime;
using Orleans.TestingHost;
using UnitTests.GrainInterfaces;
using UnitTests.Tester;
using Xunit;
using Orleans;
using Orleans.Runtime.Configuration;
using Orleans.TestingHost.Utils;

namespace Tester.Forwarding
{
    public class ShutdownSiloTests : TestClusterPerTest
    {
        public const int NumberOfSilos = 2;

        public override TestCluster CreateTestCluster()
        {
            Assert.True(StorageEmulator.TryStart());
            var options = new TestClusterOptions(NumberOfSilos);
            options.ClusterConfiguration.AddAzureBlobStorageProvider("MemoryStore", "UseDevelopmentStorage=true");
            options.ClusterConfiguration.Globals.DefaultPlacementStrategy = "ActivationCountBasedPlacement";
            options.ClusterConfiguration.Globals.NumMissedProbesLimit = 1;
            options.ClusterConfiguration.Globals.NumVotesForDeathDeclaration = 1;
            return new TestCluster(options);
        }

        [Fact, TestCategory("Forward")]
        public async Task SiloGracefulShutdown_ForwardPendingRequest()
        {
            var grain = await GetLongRunningTaskGrainOnSecondary<bool>();

            // First call should be done on Secondary
            var promisesBeforeShutdown = grain.LongRunningTask(true, TimeSpan.FromSeconds(5));
            // Second call should be transfered to another silo
            var promisesAfterShutdown = grain.LongRunningTask(true, TimeSpan.FromSeconds(5));

            // Shutdown the silo where the grain is
            await Task.Delay(500);
            HostedCluster.StopSilo(HostedCluster.SecondarySilos.First());

            await promisesBeforeShutdown;
            await promisesAfterShutdown;
        }

        [Fact, TestCategory("Functional"), TestCategory("Forward")]
        public async Task SiloGracefulShutdown_NoFailureOnGatewayShutdown()
        {
            const int numberOfGrains = 2 * NumberOfSilos;

            var grains = new List<ICounterGrain>();
            for (var i = 0; i < numberOfGrains; i++)
            {
                grains.Add(await GetCounterGrainOnPrimary());
            }

            var cancellationTokenSource = new CancellationTokenSource();
            var promise = CallIncrement(grains, cancellationTokenSource.Token);

            await Task.Delay(1000);
            HostedCluster.StopSilo(HostedCluster.SecondarySilos.First());
            await Task.Delay(1000);

            cancellationTokenSource.Cancel();

            var numberOfCalls = await promise;
            foreach (var g in grains)
            {
                var counterValue = await g.GetValue();
                Assert.True(numberOfCalls.Item1 == counterValue, $"numberOfCalls={numberOfCalls.Item1} counterValue={counterValue} exceptionCounter={numberOfCalls.Item2}");
            }
        }

        private static async Task<Tuple<int, int>> CallIncrement(IList<ICounterGrain> grains, CancellationToken token)
        {
            var counter = 0;
            var tasks = new List<Task>();
            while (!token.IsCancellationRequested)
            {
                foreach (var g in grains)
                {
                    tasks.Add(g.IncrementValue());
                }
                counter++;
                await Task.Delay(100);
            }
            var promise = Task.WhenAll(tasks);
            try
            {
                await promise;
            }
            catch (Exception)
            {
                // Nothing
            }
            if (promise.Exception != null)
            {
                Assert.True(promise.Exception.InnerExceptions.All(ex => ex.GetType() == typeof(SiloUnavailableException)));
            }
            return Tuple.Create(counter, promise.Exception?.InnerExceptions.Count ?? 0);
        }

        private async Task<ICounterGrain> GetCounterGrainOnPrimary()
        {
            while (true)
            {
                var grain = HostedCluster.GrainFactory.GetGrain<ICounterGrain>(Guid.NewGuid());
                var instanceId = await grain.GetRuntimeInstanceId();
                if (instanceId.Contains(HostedCluster.Primary.SiloAddress.Endpoint.ToString()))
                {
                    return grain;
                }
            }
        }

        private async Task<ILongRunningTaskGrain<T>> GetLongRunningTaskGrainOnSecondary<T>()
        {
            while (true)
            {
                var grain = HostedCluster.GrainFactory.GetGrain<ILongRunningTaskGrain<T>>(Guid.NewGuid());
                var instanceId = await grain.GetRuntimeInstanceId();
                if (instanceId.Contains(HostedCluster.SecondarySilos[0].SiloAddress.Endpoint.ToString()))
                {
                    return grain;
                }
            }
        }
    }
}
