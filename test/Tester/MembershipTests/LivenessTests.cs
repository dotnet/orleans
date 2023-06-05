using System.Globalization;
using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.TestingHost;
using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;
using Xunit.Abstractions;

namespace UnitTests.MembershipTests
{
    public class LivenessTestsBase : TestClusterPerTest
    {
        private readonly ITestOutputHelper output;
        private const int numAdditionalSilos = 1;
        private const int numGrains = 20;

        public LivenessTestsBase(ITestOutputHelper output)
        {
            this.output = output;
        }

        protected async Task Do_Liveness_OracleTest_1()
        {
            output.WriteLine("ClusterId= {0}", HostedCluster.Options.ClusterId);

            var silo3 = await HostedCluster.StartAdditionalSiloAsync();

            var mgmtGrain = GrainFactory.GetGrain<IManagementGrain>(0);

            var statuses = await mgmtGrain.GetHosts(false);
            foreach (var pair in statuses)
            {
                output.WriteLine("       ######## Silo {0}, status: {1}", pair.Key, pair.Value);
                Assert.Equal(SiloStatus.Active, pair.Value);
            }
            Assert.Equal(3, statuses.Count);

            var address = silo3.SiloAddress.Endpoint;
            output.WriteLine("About to stop {0}", address);
            await HostedCluster.StopSiloAsync(silo3);

            // TODO: Should we be allowing time for changes to percolate?

            output.WriteLine("----------------");

            statuses = await mgmtGrain.GetHosts(false);
            foreach (var pair in statuses)
            {
                output.WriteLine("       ######## Silo {0}, status: {1}", pair.Key, pair.Value);
                var silo = pair.Key.Endpoint;
                if (silo.Equals(address))
                {
                    Assert.True(pair.Value == SiloStatus.ShuttingDown
                        || pair.Value == SiloStatus.Stopping
                        || pair.Value == SiloStatus.Dead,
                        string.Format("SiloStatus for {0} should now be ShuttingDown or Stopping or Dead instead of {1}", silo, pair.Value));
                }
                else
                {
                    Assert.Equal(SiloStatus.Active, pair.Value);
                }
            }
        }

        protected async Task Do_Liveness_OracleTest_2(int silo2Kill, bool restart = true, bool startTimers = false)
        {
            await HostedCluster.StartAdditionalSilosAsync(numAdditionalSilos);
            await HostedCluster.WaitForLivenessToStabilizeAsync();

            for (var i = 0; i < numGrains; i++)
            {
                await SendTraffic(i + 1, startTimers);
            }

            var silo2KillHandle = HostedCluster.Silos[silo2Kill];

            logger.LogInformation("\n\n\n\nAbout to kill {Endpoint}\n\n\n", silo2KillHandle.SiloAddress.Endpoint);

            if (restart)
                await HostedCluster.RestartSiloAsync(silo2KillHandle);
            else
                await HostedCluster.KillSiloAsync(silo2KillHandle);

            var didKill = !restart;
            await HostedCluster.WaitForLivenessToStabilizeAsync(didKill);

            logger.LogInformation("\n\n\n\nAbout to start sending msg to grain again\n\n\n");

            for (var i = 0; i < numGrains; i++)
            {
                await SendTraffic(i + 1);
            }

            for (var i = numGrains; i < 2 * numGrains; i++)
            {
                await SendTraffic(i + 1);
            }
            logger.LogInformation("======================================================");
        }

        protected async Task Do_Liveness_OracleTest_3()
        {
            var moreSilos = await HostedCluster.StartAdditionalSilosAsync(1);
            await HostedCluster.WaitForLivenessToStabilizeAsync();

            await TestTraffic();

            logger.LogInformation("\n\n\n\nAbout to stop a first silo.\n\n\n");
            var siloToStop = HostedCluster.SecondarySilos[0];
            await HostedCluster.StopSiloAsync(siloToStop);

            await TestTraffic();

            logger.LogInformation("\n\n\n\nAbout to re-start a first silo.\n\n\n");
            
            await HostedCluster.RestartStoppedSecondarySiloAsync(siloToStop.Name);

            await TestTraffic();

            logger.LogInformation("\n\n\n\nAbout to stop a second silo.\n\n\n");
            await HostedCluster.StopSiloAsync(moreSilos[0]);

            await TestTraffic();

            logger.LogInformation("======================================================");
        }

        private async Task TestTraffic()
        {
            logger.LogInformation("\n\n\n\nAbout to start sending msg to grain again.\n\n\n");
            // same grains
            for (var i = 0; i < numGrains; i++)
            {
                await SendTraffic(i + 1);
            }
            // new random grains
            for (var i = 0; i < numGrains; i++)
            {
                await SendTraffic(Random.Shared.Next(10000));
            }
        }

        private async Task SendTraffic(long key, bool startTimers = false)
        {
            try
            {
                var grain = GrainFactory.GetGrain<ILivenessTestGrain>(key);
                Assert.Equal(key, grain.GetPrimaryKeyLong());
                Assert.Equal(key.ToString(CultureInfo.InvariantCulture), await grain.GetLabel());
                await LogGrainIdentity(logger, grain);
                if (startTimers)
                {
                    await grain.StartTimer();
                }
            }
            catch (Exception exc)
            {
                logger.LogInformation(exc, "Exception making grain call");
                throw;
            }
        }

        private async Task LogGrainIdentity(ILogger logger, ILivenessTestGrain grain)
        {
            logger.LogInformation("Grain {Grain}, activation {Activation} on {Host}",
                await grain.GetGrainReference(),
                await grain.GetUniqueId(),
                await grain.GetRuntimeInstanceId());
        }
    }

    public class LivenessTests_MembershipGrain : LivenessTestsBase
    {
        public LivenessTests_MembershipGrain(ITestOutputHelper output) : base(output)
        {
        }

        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            builder.AddClientBuilderConfigurator<ClientConfigurator>();
        }

        public class ClientConfigurator : IClientBuilderConfigurator
        {
            public void Configure(IConfiguration configuration, IClientBuilder clientBuilder)
            {
                clientBuilder.Configure<GatewayOptions>(options => options.PreferedGatewayIndex = 1);
            }
        }

        [Fact, TestCategory("Functional"), TestCategory("Membership")]
        public async Task Liveness_Grain_1()
        {
            await Do_Liveness_OracleTest_1();
        }

        [Fact, TestCategory("Functional"), TestCategory("Membership")]
        public async Task Liveness_Grain_2_Restart_GW()
        {
            await Do_Liveness_OracleTest_2(1);
        }

        [Fact, TestCategory("Functional"), TestCategory("Membership")]
        public async Task Liveness_Grain_3_Restart_Silo_1()
        {
            await Do_Liveness_OracleTest_2(2);
        }

        [Fact, TestCategory("Functional"), TestCategory("Membership")]
        public async Task Liveness_Grain_4_Kill_Silo_1_With_Timers()
        {
            await Do_Liveness_OracleTest_2(2, false, true);
        }

        //[Fact, TestCategory("Functional"), TestCategory("Membership")]
        /*public async Task Liveness_Grain_5_ShutdownRestartZeroLoss()
        {
            await Do_Liveness_OracleTest_3();
        }*/
    }
}
