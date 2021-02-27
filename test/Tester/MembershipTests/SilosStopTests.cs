using System;
using System.Net;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Orleans;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Runtime;
using Orleans.TestingHost;
using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;

namespace UnitTests.MembershipTests
{
    public class SilosStopTests : TestClusterPerTest
    {
        private class BuilderConfigurator : ISiloConfigurator, IClientBuilderConfigurator
        {
            public void Configure(ISiloBuilder hostBuilder)
            {
                hostBuilder
                    .Configure<ClusterMembershipOptions>(options =>
                    {
                        options.NumMissedProbesLimit = 1;
                        options.NumVotesForDeathDeclaration = 1;
                        options.TableRefreshTimeout = TimeSpan.FromSeconds(2);
                    })
                    .Configure<SiloMessagingOptions>(options => options.AssumeHomogenousSilosForTesting = true);
            }

            public void Configure(IConfiguration configuration, IClientBuilder clientBuilder)
            {
                var clusterOptions = configuration.GetTestClusterOptions();
                clientBuilder.UseStaticClustering(new IPEndPoint(IPAddress.Loopback, clusterOptions.BaseGatewayPort));
            }
        }

        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            builder.AddClientBuilderConfigurator<BuilderConfigurator>();
            builder.AddSiloBuilderConfigurator<BuilderConfigurator>();
        }

        [Fact, TestCategory("Functional"), TestCategory("Liveness")]
        public async Task SiloUngracefulShutdown_OutstandingRequestsBreak()
        {
            var grain = await GetGrainOnTargetSilo(HostedCluster.Primary);
            Assert.NotNull(grain);
            var target = await GetGrainOnTargetSilo(HostedCluster.SecondarySilos[0]);
            Assert.NotNull(target);

            var promise = grain.CallOtherLongRunningTask(target, true, TimeSpan.FromSeconds(7));

            await Task.Delay(500);
            await HostedCluster.KillSiloAsync(HostedCluster.SecondarySilos[0]);

            await Assert.ThrowsAsync<SiloUnavailableException>(() => promise);
        }

        private async Task<ILongRunningTaskGrain<bool>> GetGrainOnTargetSilo(SiloHandle siloHandle)
        {
            const int maxRetry = 10;
            for (int i = 0; i < maxRetry; i++)
            {
                var grain = GrainFactory.GetGrain<ILongRunningTaskGrain<bool>>(Guid.NewGuid());
                var instanceId = await grain.GetRuntimeInstanceId();
                if (instanceId.Contains(siloHandle.SiloAddress.Endpoint.ToString()))
                    return grain;
                await Task.Delay(100);
            }
            return null;
        }
    }
}