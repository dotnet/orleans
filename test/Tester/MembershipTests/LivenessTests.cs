using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans;
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
            output.WriteLine("ClusterId= {0}", this.HostedCluster.Options.ClusterId);

            SiloHandle silo3 = this.HostedCluster.StartAdditionalSilo();

            IManagementGrain mgmtGrain = this.GrainFactory.GetGrain<IManagementGrain>(0);

            Dictionary<SiloAddress, SiloStatus> statuses = await mgmtGrain.GetHosts(false);
            foreach (var pair in statuses)
            {
                output.WriteLine("       ######## Silo {0}, status: {1}", pair.Key, pair.Value);
                Assert.Equal(SiloStatus.Active, pair.Value);
            }
            Assert.Equal(3, statuses.Count);

            IPEndPoint address = silo3.SiloAddress.Endpoint;
            output.WriteLine("About to stop {0}", address);
            this.HostedCluster.StopSilo(silo3);

            // TODO: Should we be allowing time for changes to percolate?

            output.WriteLine("----------------");

            statuses = await mgmtGrain.GetHosts(false);
            foreach (var pair in statuses)
            {
                output.WriteLine("       ######## Silo {0}, status: {1}", pair.Key, pair.Value);
                IPEndPoint silo = pair.Key.Endpoint;
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
            await this.HostedCluster.StartAdditionalSilos(numAdditionalSilos);
            await this.HostedCluster.WaitForLivenessToStabilizeAsync();

            for (int i = 0; i < numGrains; i++)
            {
                await SendTraffic(i + 1, startTimers);
            }

            SiloHandle silo2KillHandle = this.HostedCluster.Silos[silo2Kill];

            logger.Info("\n\n\n\nAbout to kill {0}\n\n\n", silo2KillHandle.SiloAddress.Endpoint);

            if (restart)
                this.HostedCluster.RestartSilo(silo2KillHandle);
            else
                this.HostedCluster.KillSilo(silo2KillHandle);

            bool didKill = !restart;
            await this.HostedCluster.WaitForLivenessToStabilizeAsync(didKill);

            logger.Info("\n\n\n\nAbout to start sending msg to grain again\n\n\n");

            for (int i = 0; i < numGrains; i++)
            {
                await SendTraffic(i + 1);
            }

            for (int i = numGrains; i < 2 * numGrains; i++)
            {
                await SendTraffic(i + 1);
            }
            logger.Info("======================================================");
        }

        protected async Task Do_Liveness_OracleTest_3()
        {
            var moreSilos = await this.HostedCluster.StartAdditionalSilos(1);
            await this.HostedCluster.WaitForLivenessToStabilizeAsync();

            await TestTraffic();

            logger.Info("\n\n\n\nAbout to stop a first silo.\n\n\n");
            var siloToStop = this.HostedCluster.SecondarySilos[0];
            this.HostedCluster.StopSilo(siloToStop);

            await TestTraffic();

            logger.Info("\n\n\n\nAbout to re-start a first silo.\n\n\n");
            
            this.HostedCluster.RestartStoppedSecondarySilo(siloToStop.Name);

            await TestTraffic();

            logger.Info("\n\n\n\nAbout to stop a second silo.\n\n\n");
            this.HostedCluster.StopSilo(moreSilos[0]);

            await TestTraffic();

            logger.Info("======================================================");
        }

        private async Task TestTraffic()
        {
            logger.Info("\n\n\n\nAbout to start sending msg to grain again.\n\n\n");
            // same grains
            for (int i = 0; i < numGrains; i++)
            {
                await SendTraffic(i + 1);
            }
            // new random grains
            for (int i = 0; i < numGrains; i++)
            {
                await SendTraffic(random.Next(10000));
            }
        }

        private async Task SendTraffic(long key, bool startTimers = false)
        {
            try
            {
                ILivenessTestGrain grain = this.GrainFactory.GetGrain<ILivenessTestGrain>(key);
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
                logger.Info("Exception making grain call: {0}", exc);
                throw;
            }
        }

        private async Task LogGrainIdentity(ILogger logger, ILivenessTestGrain grain)
        {
            logger.Info("Grain {0}, activation {1} on {2}",
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
            builder.ConfigureLegacyConfiguration(legacy =>
            {
                legacy.ClientConfiguration.PreferedGatewayIndex = 1;
            });
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
