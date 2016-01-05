using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.TestingHost;
using UnitTests.GrainInterfaces;
using UnitTests.Tester;
using Orleans.SqlUtils;
using UnitTests.General;

namespace UnitTests.MembershipTests
{
    public class LivenessTestsBase : UnitTestSiloHost
    {
        private const int numAdditionalSilos = 1;
        private const int numGrains = 20;

        public TestContext TestContext { get; set; }

        protected LivenessTestsBase(TestingSiloOptions siloOptions)
            : base(siloOptions)
        { }

        protected LivenessTestsBase(TestingSiloOptions siloOptions, TestingClientOptions clientOptions)
            : base(siloOptions, clientOptions)
        { }

        protected void DoTestCleanup()
        {
            Console.WriteLine("Test {0} completed - Outcome = {1}", TestContext.TestName, TestContext.CurrentTestOutcome);
            StopAllSilos();
        }

        protected static void DoClassCleanup()
        {
            Console.WriteLine("ClassCleanup.");
            StopAllSilos();
        }

        protected static void DoClassInitialize()
        {
            Console.WriteLine("ClassCleanup.");
            StopAllSilos();
        }

        protected async Task Do_Liveness_OracleTest_1()
        {
            Console.WriteLine("DeploymentId= {0}", DeploymentId);

            SiloHandle silo3 = StartAdditionalSilo();

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
            StopSilo(silo3);

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
            List<SiloHandle> moreSilos = StartAdditionalSilos(numAdditionalSilos);
            await WaitForLivenessToStabilizeAsync();

            for (int i = 0; i < numGrains; i++)
            {
                await SendTraffic(i + 1, startTimers);
            }

            SiloHandle silo2KillHandle;
            if (silo2Kill == 0)
                silo2KillHandle = Primary;
            else if (silo2Kill == 1)
                silo2KillHandle = Secondary;
            else
                silo2KillHandle = moreSilos[silo2Kill - 2];

            logger.Info("\n\n\n\nAbout to kill {0}\n\n\n", silo2KillHandle.Endpoint);

            if (restart)
                RestartSilo(silo2KillHandle);
            else
                KillSilo(silo2KillHandle);

            bool didKill = !restart;
            await WaitForLivenessToStabilizeAsync(didKill);

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
            List<SiloHandle> moreSilos = StartAdditionalSilos(1);
            await WaitForLivenessToStabilizeAsync();

            await TestTraffic();

            logger.Info("\n\n\n\nAbout to stop a first silo.\n\n\n");
            TestingSiloOptions secondarySiloOptions = Secondary.Options;
            StopSilo(Secondary);

            await TestTraffic();

            logger.Info("\n\n\n\nAbout to re-start a first silo.\n\n\n");
            StartSecondarySilo(secondarySiloOptions, 1);

            await TestTraffic();

            logger.Info("\n\n\n\nAbout to stop a second silo.\n\n\n");
            StopSilo(moreSilos[0]);

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

        private static async Task LogGrainIdentity(Logger logger, ILivenessTestGrain grain)
        {
            logger.Info("Grain {0}, activation {1} on {2}",
                await grain.GetGrainReference(),
                await grain.GetUniqueId(),
                await grain.GetRuntimeInstanceId());
        }
    }

    [TestClass]
    public class LivenessTests_MembershipGrain : LivenessTestsBase
    {
        private static readonly TestingSiloOptions siloOptions = new TestingSiloOptions
        {
            StartFreshOrleans = true,
            StartPrimary = true,
            StartSecondary = true,
            LivenessType = GlobalConfiguration.LivenessProviderType.MembershipTableGrain,
            ReminderServiceType = GlobalConfiguration.ReminderServiceProviderType.ReminderTableGrain
        };

        private static readonly TestingClientOptions clientOptions = new TestingClientOptions
        {
            ProxiedGateway = true,
            Gateways = new List<IPEndPoint>(new[]
                    {
                        new IPEndPoint(IPAddress.Loopback, TestingSiloHost.ProxyBasePort), 
                        new IPEndPoint(IPAddress.Loopback, TestingSiloHost.ProxyBasePort + 1)
                    }),
            PreferedGatewayIndex = 1
        };

        public LivenessTests_MembershipGrain()
            : base(siloOptions, clientOptions)
        { }

        [TestInitialize]
        public void TestInitialize()
        {
            DoTestCleanup();
        }

        [TestCleanup]
        public void TestCleanup()
        {
            DoTestCleanup();
        }

        [ClassCleanup]
        public static void MyClassCleanup()
        {
            DoClassCleanup();
        }

        [ClassInitialize]
        public static void MyClassInitialize(TestContext testContext)
        {
            DoClassInitialize();
        }

		[TestMethod, TestCategory("Functional"), TestCategory("Liveness")]
        public void Silo_Config_MembershipGrain()
        {
            Assert.AreEqual(GlobalConfiguration.LivenessProviderType.MembershipTableGrain, Globals.LivenessType, "LivenessType");
            Assert.AreEqual(GlobalConfiguration.ReminderServiceProviderType.ReminderTableGrain, Globals.ReminderServiceType, "ReminderServiceType");
        }

        //[TestMethod, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Membership"), TestCategory("Gabi")]
        public async Task Liveness_Grain_1()
        {
            await Do_Liveness_OracleTest_1();
        }

        //[TestMethod, TestCategory("Functional"), TestCategory("Membership")]
        public async Task Liveness_Grain_2_Restart_GW()
        {
            await Do_Liveness_OracleTest_2(1);
        }

        //[TestMethod, TestCategory("Functional"), TestCategory("Membership")]
        public async Task Liveness_Grain_3_Restart_Silo_1()
        {
            await Do_Liveness_OracleTest_2(2);
        }

        //[TestMethod, TestCategory("Functional"), TestCategory("Membership")]
        public async Task Liveness_Grain_4_Kill_Silo_1_With_Timers()
        {
            await Do_Liveness_OracleTest_2(2, false, true);
        }

        //[TestMethod, TestCategory("Functional"), TestCategory("Membership")]
        public async Task Liveness_Grain_5_ShutdownRestartZeroLoss()
        {
            await Do_Liveness_OracleTest_3();
        }
    }

    [TestClass]
    public class LivenessTests_AzureTable : LivenessTestsBase
    {
        private static readonly TestingSiloOptions siloOptions = new TestingSiloOptions
        {
            StartFreshOrleans = true,
            StartPrimary = true,
            StartSecondary = true,
            DataConnectionString = StorageTestConstants.DataConnectionString,
            LivenessType = GlobalConfiguration.LivenessProviderType.AzureTable,
            ReminderServiceType = GlobalConfiguration.ReminderServiceProviderType.ReminderTableGrain
        };

        public LivenessTests_AzureTable()
            : base(siloOptions)
        { }

        [TestCleanup]
        public void TestCleanup()
        {
            DoTestCleanup();
        }

        [ClassCleanup]
        public static void MyClassCleanup()
        {
            DoClassCleanup();
        }

        [ClassInitialize]
        public static void MyClassInitialize(TestContext testContext)
        {
            CheckForAzureStorage();

            DoClassInitialize();
        }
		
		[TestMethod, TestCategory("Functional"), TestCategory("Liveness"), TestCategory("Azure")]
        public void Silo_Config_AzureTable()
        {
            Assert.AreEqual(GlobalConfiguration.LivenessProviderType.AzureTable, Globals.LivenessType, "LivenessType");
            Assert.AreEqual(GlobalConfiguration.ReminderServiceProviderType.ReminderTableGrain, Globals.ReminderServiceType, "ReminderServiceType");
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Membership"), TestCategory("Azure")]
        public async Task Liveness_Azure_1()
        {
            await Do_Liveness_OracleTest_1();
        }

        //[TestMethod, TestCategory("Functional"), TestCategory("Membership"), TestCategory("Azure")]
        public async Task Liveness_Azure_2_Restart_Primary()
        {
            await Do_Liveness_OracleTest_2(0);
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Membership"), TestCategory("Azure")]
        public async Task Liveness_Azure_3_Restart_GW()
        {
            await Do_Liveness_OracleTest_2(1);
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Membership"), TestCategory("Azure")]
        public async Task Liveness_Azure_4_Restart_Silo_1()
        {
            await Do_Liveness_OracleTest_2(2);
        }

       // [TestMethod, TestCategory("Functional"), TestCategory("Membership"), TestCategory("Azure")]
        public async Task Liveness_Azure_5_Kill_Silo_1_With_Timers()
        {
            await Do_Liveness_OracleTest_2(2, false, true);
        }
    }

    [TestClass]
    public class LivenessTests_ZK : LivenessTestsBase
    {
        private static readonly TestingSiloOptions siloOptions = new TestingSiloOptions
        {
            StartFreshOrleans = true,
            StartPrimary = true,
            StartSecondary = true,
            DataConnectionString = StorageTestConstants.DataConnectionString,
            LivenessType = GlobalConfiguration.LivenessProviderType.AzureTable,
            ReminderServiceType = GlobalConfiguration.ReminderServiceProviderType.ReminderTableGrain
        };

        public LivenessTests_ZK()
            : base(siloOptions)
        { }

        [TestCleanup]
        public void TestCleanup()
        {
            DoTestCleanup();
        }

        [ClassCleanup]
        public static void MyClassCleanup()
        {
            DoClassCleanup();
        }

        [ClassInitialize]
        public static void MyClassInitialize(TestContext testContext)
        {
            DoClassInitialize();
        }

        //[TestMethod,  TestCategory("Membership"), TestCategory("ZooKeeper")]
        public async Task Liveness_ZooKeeper_1()
        {
            await Do_Liveness_OracleTest_1();
        }

        //[TestMethod, TestCategory("Membership"), TestCategory("ZooKeeper")]
        public async Task Liveness_ZooKeeper_2_Restart_Primary()
        {
            await Do_Liveness_OracleTest_2(0);
        }

        //[TestMethod, TestCategory("Membership"), TestCategory("ZooKeeper")]
        public async Task Liveness_ZooKeeper_3_Restart_GW()
        {
            await Do_Liveness_OracleTest_2(1);
        }

        //[TestMethod, TestCategory("Membership"), TestCategory("ZooKeeper")]
        public async Task Liveness_ZooKeeper_4_Restart_Silo_1()
        {
            await Do_Liveness_OracleTest_2(2);
        }

        //[TestMethod, TestCategory("Membership"), TestCategory("ZooKeeper")]
        public async Task Liveness_ZooKeeper_5_Kill_Silo_1_With_Timers()
        {
            await Do_Liveness_OracleTest_2(2, false, true);
        }
    }

    [TestClass]    
    public class LivenessTests_SqlServer : LivenessTestsBase
    {
        private const string testDatabaseName = "OrleansTest";

        private static readonly TestingSiloOptions siloOptions = new TestingSiloOptions
        {
            StartFreshOrleans = true,
            StartPrimary = true,
            StartSecondary = true,
            DataConnectionString = "Set-in-ClassInitialize",
            LivenessType = GlobalConfiguration.LivenessProviderType.SqlServer,
            ReminderServiceType = GlobalConfiguration.ReminderServiceProviderType.ReminderTableGrain
        };

        public LivenessTests_SqlServer()
            : base(siloOptions)
        { }

        [ClassInitialize]
        public static void ClassInitialize(TestContext context)
        {
            Console.WriteLine("TestContext.DeploymentDirectory={0}", context.DeploymentDirectory);
            Console.WriteLine("TestContext=");
            Console.WriteLine(DumpTestContext(context));

            Console.WriteLine("Initializing relational databases...");
            var relationalStorage = RelationalStorageForTesting.SetupInstance(AdoNetInvariants.InvariantNameSqlServer, testDatabaseName).Result;

            siloOptions.DataConnectionString = relationalStorage.CurrentConnectionString;
        }

        [TestCleanup]
        public void TestCleanup()
        {
            DoTestCleanup();
        }

        [ClassCleanup]
        public static void MyClassCleanup()
        {
            DoClassCleanup();
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Liveness"), TestCategory("SqlServer")]
        public void Silo_Config_SqlServer()
        {
            Assert.AreEqual(GlobalConfiguration.LivenessProviderType.SqlServer, Globals.LivenessType, "LivenessType");
            Assert.AreEqual(GlobalConfiguration.ReminderServiceProviderType.ReminderTableGrain, Globals.ReminderServiceType, "ReminderServiceType");
        }
		
        //[TestMethod, TestCategory("Membership"), TestCategory("SqlServer")]
        public async Task Liveness_Sql_1()
        {
            await Do_Liveness_OracleTest_1();
        }

        //[TestMethod, TestCategory("Membership"), TestCategory("SqlServer")]
        public async Task Liveness_Sql_2_Restart_Primary()
        {
            await Do_Liveness_OracleTest_2(0);
        }

        //[TestMethod, TestCategory("Membership"), TestCategory("SqlServer")]
        public async Task Liveness_Sql_3_Restartl_GW()
        {
            await Do_Liveness_OracleTest_2(1);
        }

        //[TestMethod, TestCategory("Membership"), TestCategory("SqlServer")]
        public async Task Liveness_Sql_4_Restart_Silo_1()
        {
            await Do_Liveness_OracleTest_2(2);
        }

        //[TestMethod, TestCategory("Membership"), TestCategory("SqlServer")]
        public async Task Liveness_Sql_5_Kill_Silo_1_With_Timers()
        {
            await Do_Liveness_OracleTest_2(2, false, true);
        }
    }
}
