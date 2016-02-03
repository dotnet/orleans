using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Threading.Tasks;
using Orleans;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.TestingHost;
using UnitTests.GrainInterfaces;
using UnitTests.Tester;
using Orleans.SqlUtils;
using Tester;
using UnitTests.General;
using Assert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;
using Xunit;

namespace UnitTests.MembershipTests
{
    public class LivenessTestsBase : HostedTestClusterPerTest
    {
        private const int numAdditionalSilos = 1;
        private const int numGrains = 20;

        protected async Task Do_Liveness_OracleTest_1()
        {
            Console.WriteLine("DeploymentId= {0}", this.HostedCluster.DeploymentId);

            SiloHandle silo3 = this.HostedCluster.StartAdditionalSilo();

            IManagementGrain mgmtGrain = GrainClient.GrainFactory.GetGrain<IManagementGrain>(RuntimeInterfaceConstants.SYSTEM_MANAGEMENT_ID);

            Dictionary<SiloAddress, SiloStatus> statuses = await mgmtGrain.GetHosts(false);
            foreach (var pair in statuses)
            {
                Console.WriteLine("       ######## Silo {0}, status: {1}", pair.Key, pair.Value);
                Assert.AreEqual(SiloStatus.Active, pair.Value);
            }
            Assert.AreEqual(3, statuses.Count);

            IPEndPoint address = silo3.Endpoint;
            Console.WriteLine("About to stop {0}", address);
            this.HostedCluster.StopSilo(silo3);

            // TODO: Should we be allowing time for changes to percolate?

            Console.WriteLine("----------------");

            statuses = await mgmtGrain.GetHosts(false);
            foreach (var pair in statuses)
            {
                Console.WriteLine("       ######## Silo {0}, status: {1}", pair.Key, pair.Value);
                IPEndPoint silo = pair.Key.Endpoint;
                if (silo.Equals(address))
                {
                    Assert.IsTrue(pair.Value.Equals(SiloStatus.ShuttingDown)
                        || pair.Value.Equals(SiloStatus.Stopping)
                        || pair.Value.Equals(SiloStatus.Dead),
                        "SiloStatus for {0} should now be ShuttingDown or Stopping or Dead instead of {1}",
                        silo, pair.Value);
                }
                else
                {
                    Assert.AreEqual(SiloStatus.Active, pair.Value, "SiloStatus for {0}", silo);
                }
            }
        }

        protected async Task Do_Liveness_OracleTest_2(int silo2Kill, bool restart = true, bool startTimers = false)
        {
            List<SiloHandle> moreSilos = this.HostedCluster.StartAdditionalSilos(numAdditionalSilos);
            await this.HostedCluster.WaitForLivenessToStabilizeAsync();

            for (int i = 0; i < numGrains; i++)
            {
                await SendTraffic(i + 1, startTimers);
            }

            SiloHandle silo2KillHandle;
            if (silo2Kill == 0)
                silo2KillHandle = this.HostedCluster.Primary;
            else if (silo2Kill == 1)
                silo2KillHandle = this.HostedCluster.Secondary;
            else
                silo2KillHandle = moreSilos[silo2Kill - 2];

            logger.Info("\n\n\n\nAbout to kill {0}\n\n\n", silo2KillHandle.Endpoint);

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
            List<SiloHandle> moreSilos = this.HostedCluster.StartAdditionalSilos(1);
            await this.HostedCluster.WaitForLivenessToStabilizeAsync();

            await TestTraffic();

            logger.Info("\n\n\n\nAbout to stop a first silo.\n\n\n");
            TestingSiloOptions secondarySiloOptions = this.HostedCluster.Secondary.Options;
            this.HostedCluster.StopSilo(this.HostedCluster.Secondary);

            await TestTraffic();

            logger.Info("\n\n\n\nAbout to re-start a first silo.\n\n\n");
            this.HostedCluster.StartSecondarySilo(secondarySiloOptions, 1);

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
                ILivenessTestGrain grain = GrainClient.GrainFactory.GetGrain<ILivenessTestGrain>(key);
                Assert.AreEqual(key, grain.GetPrimaryKeyLong());
                Assert.AreEqual(key.ToString(CultureInfo.InvariantCulture), await grain.GetLabel());
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

        private async Task LogGrainIdentity(Logger logger, ILivenessTestGrain grain)
        {
            logger.Info("Grain {0}, activation {1} on {2}",
                await grain.GetGrainReference(),
                await grain.GetUniqueId(),
                await grain.GetRuntimeInstanceId());
        }
    }

    public class LivenessTests_MembershipGrain : LivenessTestsBase
    {
        public override TestingSiloHost CreateSiloHost()
        {
            var siloOptions = new TestingSiloOptions
            {
                StartFreshOrleans = true,
                StartPrimary = true,
                StartSecondary = true,
                LivenessType = GlobalConfiguration.LivenessProviderType.MembershipTableGrain,
                ReminderServiceType = GlobalConfiguration.ReminderServiceProviderType.ReminderTableGrain
            };

            var clientOptions = new TestingClientOptions
            {
                ProxiedGateway = true,
                Gateways = new List<IPEndPoint>(new[]
                    {
                        new IPEndPoint(IPAddress.Loopback, TestingSiloHost.ProxyBasePort),
                        new IPEndPoint(IPAddress.Loopback, TestingSiloHost.ProxyBasePort + 1)
                    }),
                PreferedGatewayIndex = 1
            };

            return new TestingSiloHost(siloOptions, clientOptions);
        }
        
		[Fact, TestCategory("Functional"), TestCategory("Liveness")]
        public void Silo_Config_MembershipGrain()
        {
            Assert.AreEqual(GlobalConfiguration.LivenessProviderType.MembershipTableGrain, this.HostedCluster.Globals.LivenessType, "LivenessType");
            Assert.AreEqual(GlobalConfiguration.ReminderServiceProviderType.ReminderTableGrain, this.HostedCluster.Globals.ReminderServiceType, "ReminderServiceType");
        }

        //[Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Membership"), TestCategory("Gabi")]
        public async Task Liveness_Grain_1()
        {
            await Do_Liveness_OracleTest_1();
        }

        //[Fact, TestCategory("Functional"), TestCategory("Membership")]
        public async Task Liveness_Grain_2_Restart_GW()
        {
            await Do_Liveness_OracleTest_2(1);
        }

        //[Fact, TestCategory("Functional"), TestCategory("Membership")]
        public async Task Liveness_Grain_3_Restart_Silo_1()
        {
            await Do_Liveness_OracleTest_2(2);
        }

        //[Fact, TestCategory("Functional"), TestCategory("Membership")]
        public async Task Liveness_Grain_4_Kill_Silo_1_With_Timers()
        {
            await Do_Liveness_OracleTest_2(2, false, true);
        }

        //[Fact, TestCategory("Functional"), TestCategory("Membership")]
        public async Task Liveness_Grain_5_ShutdownRestartZeroLoss()
        {
            await Do_Liveness_OracleTest_3();
        }
    }

    public class LivenessTests_AzureTable : LivenessTestsBase
    {
        public override TestingSiloHost CreateSiloHost()
        {
            TestUtils.CheckForAzureStorage();

            var siloOptions = new TestingSiloOptions
            {
                StartFreshOrleans = true,
                StartPrimary = true,
                StartSecondary = true,
                DataConnectionString = StorageTestConstants.DataConnectionString,
                LivenessType = GlobalConfiguration.LivenessProviderType.AzureTable,
                ReminderServiceType = GlobalConfiguration.ReminderServiceProviderType.ReminderTableGrain
            };

            return new TestingSiloHost(siloOptions);
        }


        [Fact, TestCategory("Functional"), TestCategory("Liveness"), TestCategory("Azure")]
        public void Silo_Config_AzureTable()
        {
            Assert.AreEqual(GlobalConfiguration.LivenessProviderType.AzureTable, this.HostedCluster.Globals.LivenessType, "LivenessType");
            Assert.AreEqual(GlobalConfiguration.ReminderServiceProviderType.ReminderTableGrain, this.HostedCluster.Globals.ReminderServiceType, "ReminderServiceType");
        }

        [Fact, TestCategory("Functional"), TestCategory("Membership"), TestCategory("Azure")]
        public async Task Liveness_Azure_1()
        {
            await Do_Liveness_OracleTest_1();
        }

        //[Fact, TestCategory("Functional"), TestCategory("Membership"), TestCategory("Azure")]
        public async Task Liveness_Azure_2_Restart_Primary()
        {
            await Do_Liveness_OracleTest_2(0);
        }

        [Fact, TestCategory("Functional"), TestCategory("Membership"), TestCategory("Azure")]
        public async Task Liveness_Azure_3_Restart_GW()
        {
            await Do_Liveness_OracleTest_2(1);
        }

        [Fact, TestCategory("Functional"), TestCategory("Membership"), TestCategory("Azure")]
        public async Task Liveness_Azure_4_Restart_Silo_1()
        {
            await Do_Liveness_OracleTest_2(2);
        }

        // [Fact, TestCategory("Functional"), TestCategory("Membership"), TestCategory("Azure")]
        public async Task Liveness_Azure_5_Kill_Silo_1_With_Timers()
        {
            await Do_Liveness_OracleTest_2(2, false, true);
        }
    }

    public class LivenessTests_ZK : LivenessTestsBase
    {
        public override TestingSiloHost CreateSiloHost()
        {
            TestUtils.CheckForAzureStorage();

            var siloOptions = new TestingSiloOptions
            {
                StartFreshOrleans = true,
                StartPrimary = true,
                StartSecondary = true,
                DataConnectionString = StorageTestConstants.DataConnectionString,
                LivenessType = GlobalConfiguration.LivenessProviderType.ZooKeeper,
                ReminderServiceType = GlobalConfiguration.ReminderServiceProviderType.ReminderTableGrain
            };

            return new TestingSiloHost(siloOptions);
        }

        //[Fact,  TestCategory("Membership"), TestCategory("ZooKeeper")]
        public async Task Liveness_ZooKeeper_1()
        {
            await Do_Liveness_OracleTest_1();
        }

        //[Fact, TestCategory("Membership"), TestCategory("ZooKeeper")]
        public async Task Liveness_ZooKeeper_2_Restart_Primary()
        {
            await Do_Liveness_OracleTest_2(0);
        }

        //[Fact, TestCategory("Membership"), TestCategory("ZooKeeper")]
        public async Task Liveness_ZooKeeper_3_Restart_GW()
        {
            await Do_Liveness_OracleTest_2(1);
        }

        //[Fact, TestCategory("Membership"), TestCategory("ZooKeeper")]
        public async Task Liveness_ZooKeeper_4_Restart_Silo_1()
        {
            await Do_Liveness_OracleTest_2(2);
        }

        //[Fact, TestCategory("Membership"), TestCategory("ZooKeeper")]
        public async Task Liveness_ZooKeeper_5_Kill_Silo_1_With_Timers()
        {
            await Do_Liveness_OracleTest_2(2, false, true);
        }
    }

    public class LivenessTests_SqlServer : LivenessTestsBase
    {
        public const string TestDatabaseName = "OrleansTest";

        public override TestingSiloHost CreateSiloHost()
        {
            //Console.WriteLine("Initializing relational databases...");
            var relationalStorage = RelationalStorageForTesting.SetupInstance(AdoNetInvariants.InvariantNameSqlServer, TestDatabaseName).Result;

            var siloOptions = new TestingSiloOptions
            {
                StartFreshOrleans = true,
                StartPrimary = true,
                StartSecondary = true,
                DataConnectionString = relationalStorage.CurrentConnectionString,
                LivenessType = GlobalConfiguration.LivenessProviderType.SqlServer,
                ReminderServiceType = GlobalConfiguration.ReminderServiceProviderType.ReminderTableGrain
            };

            //Console.WriteLine("TestContext.DeploymentDirectory={0}", context.DeploymentDirectory);
            //Console.WriteLine("TestContext=");
            //Console.WriteLine(TestUtils.DumpTestContext(context));

            return new TestingSiloHost(siloOptions);
        }

		[Fact, TestCategory("Functional"), TestCategory("Liveness"), TestCategory("SqlServer")]
        public void Silo_Config_SqlServer()
        {
            Assert.AreEqual(GlobalConfiguration.LivenessProviderType.SqlServer, this.HostedCluster.Globals.LivenessType, "LivenessType");
            Assert.AreEqual(GlobalConfiguration.ReminderServiceProviderType.ReminderTableGrain, this.HostedCluster.Globals.ReminderServiceType, "ReminderServiceType");
        }
		
        //[Fact, TestCategory("Membership"), TestCategory("SqlServer")]
        public async Task Liveness_Sql_1()
        {
            await Do_Liveness_OracleTest_1();
        }

        //[Fact, TestCategory("Membership"), TestCategory("SqlServer")]
        public async Task Liveness_Sql_2_Restart_Primary()
        {
            await Do_Liveness_OracleTest_2(0);
        }

        //[Fact, TestCategory("Membership"), TestCategory("SqlServer")]
        public async Task Liveness_Sql_3_Restartl_GW()
        {
            await Do_Liveness_OracleTest_2(1);
        }

        //[Fact, TestCategory("Membership"), TestCategory("SqlServer")]
        public async Task Liveness_Sql_4_Restart_Silo_1()
        {
            await Do_Liveness_OracleTest_2(2);
        }

        //[Fact, TestCategory("Membership"), TestCategory("SqlServer")]
        public async Task Liveness_Sql_5_Kill_Silo_1_With_Timers()
        {
            await Do_Liveness_OracleTest_2(2, false, true);
        }
    }
}
