using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Threading.Tasks;
using NUnit.Framework;
using Orleans;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.TestingHost;
using UnitTests.GrainInterfaces;
using UnitTests.Tester;
using Orleans.Runtime.Storage.Relational;
using UnitTests.General;

namespace UnitTests.MembershipTests
{
    public class LivenessTestsBase : UnitTestSiloHost
    {
        private static readonly TestingSiloOptions noStart = new TestingSiloOptions
        {
            StartPrimary = false,
            StartSecondary = false,
            StartClient = false,
        };

        private const int numAdditionalSilos = 1;
        private const int numGrains = 20;

        protected LivenessTestsBase()
            : base(noStart)
        {
            // For Liveness tests, we always want to start with known silo setup.
            // Each test will start new set of silos in TestInitialize and stop them in TestCleanup.
        }

        protected void DoTestInitialize(TestingSiloOptions siloOptions)
        {
            Console.WriteLine("Test-DoTestInitialize.");
            // Regardless of the membership type that the silos use, 
            // they always listen on the same TCP gateway ports,
            // so we use that apriori data explicitly here.
            TestingClientOptions clientOptions = new TestingClientOptions
            {
                ProxiedGateway = true,
                Gateways = new List<IPEndPoint>(new[]
                {
                    new IPEndPoint(IPAddress.Loopback, ProxyBasePort), 
                    new IPEndPoint(IPAddress.Loopback, ProxyBasePort + 1)
                }),
                PreferedGatewayIndex = 1
            };

            base.InitializeAsync(siloOptions, clientOptions).Wait();
        }

        protected void DoTestCleanup()
        {
            Console.WriteLine("Test-DoTestCleanup.");
            TestContext testContext = TestContext.CurrentContext;
            Console.WriteLine("Test {0} completed - Outcome = {1}", testContext.Test.Name, testContext.Result.Status);
            StopAllSilos();
        }

        [TestFixtureSetUp]
        public virtual void ClassInitialize()
        {
            Console.WriteLine("Test-ClassInitialize.");
            //StopAllSilos();
        }

        [TestFixtureTearDown]
        public void ClassCleanup()
        {
            Console.WriteLine("Test-ClassCleanup.");
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

    [TestFixture]
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

        [SetUp]
        public void TestInitialize()
        {
            DoTestInitialize(siloOptions);
        }

        [TearDown]
        public void TestCleanup()
        {
            DoTestCleanup();
        }

        [Test, Category("BVT"), Category("Membership"), Category("MembershipGrain")]
        public async Task Liveness_Grain_1()
        {
            await Do_Liveness_OracleTest_1();
        }

        //[Category("Functional")]
        [Test, Category("Membership"), Category("MembershipGrain")]
        public async Task Liveness_Grain_2_Restart_GW()
        {
            await Do_Liveness_OracleTest_2(1);
        }

        //[Category("Functional")]
        [Test, Category("Membership"), Category("MembershipGrain")]
        public async Task Liveness_Grain_3_Restart_Silo_1()
        {
            await Do_Liveness_OracleTest_2(2);
        }

        //[Category("Functional")]
        [Test, Category("Membership"), Category("MembershipGrain")]
        public async Task Liveness_Grain_4_Kill_Silo_1_With_Timers()
        {
            await Do_Liveness_OracleTest_2(2, false, true);
        }

        //[Category("Functional")]
        [Test, Category("Membership"), Category("MembershipGrain")]
        public async Task Liveness_Grain_5_ShutdownRestartZeroLoss()
        {
            await Do_Liveness_OracleTest_3();
        }
    }

    [TestFixture]
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

        [SetUp]
        public void TestInitialize()
        {
            DoTestInitialize(siloOptions);
        }

        [TearDown]
        public void TestCleanup()
        {
            DoTestCleanup();
        }

        //[Category("Functional")]
        [Test, Category("Membership"), Category("Azure")]
        public async Task Liveness_Azure_1()
        {
            await Do_Liveness_OracleTest_1();
        }

        //[Category("Functional")]
        [Test, Category("Membership"), Category("Azure")]
        public async Task Liveness_Azure_2_Restart_Primary()
        {
            await Do_Liveness_OracleTest_2(0);
        }

        //[Category("Functional")]
        [Test, Category("Membership"), Category("Azure")]
        public async Task Liveness_Azure_3_Restart_GW()
        {
            await Do_Liveness_OracleTest_2(1);
        }

        //[Category("Functional")]
        [Test, Category("Membership"), Category("Azure")]
        public async Task Liveness_Azure_4_Restart_Silo_1()
        {
            await Do_Liveness_OracleTest_2(2);
        }

        //[Category("Functional")]
        [Test, Category("Membership"), Category("Azure")]
        public async Task Liveness_Azure_5_Kill_Silo_1_With_Timers()
        {
            await Do_Liveness_OracleTest_2(2, false, true);
        }
    }

    [TestFixture]
    public class LivenessTests_ZK : LivenessTestsBase
    {
        private static readonly TestingSiloOptions siloOptions = new TestingSiloOptions
        {
            StartFreshOrleans = true,
            StartPrimary = true,
            StartSecondary = true,
            DataConnectionString = StorageTestConstants.DataConnectionString,
            LivenessType = GlobalConfiguration.LivenessProviderType.ZooKeeper,
            ReminderServiceType = GlobalConfiguration.ReminderServiceProviderType.ReminderTableGrain
        };

        [SetUp]
        public void TestInitialize()
        {
            DoTestInitialize(siloOptions);
        }

        [TearDown]
        public void TestCleanup()
        {
            DoTestCleanup();
        }

        [Test, Category("Membership"), Category("ZooKeeper")]
        public async Task Liveness_ZooKeeper_1()
        {
            await Do_Liveness_OracleTest_1();
        }

        [Test, Category("Membership"), Category("ZooKeeper")]
        public async Task Liveness_ZooKeeper_2_Restart_Primary()
        {
            await Do_Liveness_OracleTest_2(0);
        }

        [Test, Category("Membership"), Category("ZooKeeper")]
        public async Task Liveness_ZooKeeper_3_Restart_GW()
        {
            await Do_Liveness_OracleTest_2(1);
        }

        [Test, Category("Membership"), Category("ZooKeeper")]
        public async Task Liveness_ZooKeeper_4_Restart_Silo_1()
        {
            await Do_Liveness_OracleTest_2(2);
        }

        [Test, Category("Membership"), Category("ZooKeeper")]
        public async Task Liveness_ZooKeeper_5_Kill_Silo_1_With_Timers()
        {
            await Do_Liveness_OracleTest_2(2, false, true);
        }
    }

    [TestFixture]
    public class LivenessTests_SqlServer : LivenessTestsBase
    {
        private IRelationalStorage relationalStorage;
        private const string testDatabaseName = "OrleansTest";

        private static readonly TestingSiloOptions siloOptions = new TestingSiloOptions
        {
            StartFreshOrleans = true,
            StartPrimary = true,
            StartSecondary = true,
            DataConnectionString = "NOT-SET",
            LivenessType = GlobalConfiguration.LivenessProviderType.SqlServer,
            ReminderServiceType = GlobalConfiguration.ReminderServiceProviderType.ReminderTableGrain
        };

        [TestFixtureSetUp]
        public override void ClassInitialize()
        {
            base.ClassInitialize();

            Console.WriteLine("TestContext=");
            TestContext testContext = TestContext.CurrentContext;
            Console.WriteLine(DumpTestContext(testContext));

            Console.WriteLine("Initializing relational databases...");
            relationalStorage = SqlTestsEnvironment.Setup(testDatabaseName);
                        
            siloOptions.DataConnectionString = relationalStorage.ConnectionString;
        }

        [SetUp]
        public void TestInitialize()
        {
            DoTestInitialize(siloOptions);
        }

        [TearDown]
        public void TestCleanup()
        {
            DoTestCleanup();
        }

        [Test, Category("Membership"), Category("SqlServer")]
        public async Task Liveness_Sql_1()
        {
            await Do_Liveness_OracleTest_1();
        }

        [Test, Category("Membership"), Category("SqlServer")]
        public async Task Liveness_Sql_2_Restart_Primary()
        {
            await Do_Liveness_OracleTest_2(0);
        }

        [Test, Category("Membership"), Category("SqlServer")]
        public async Task Liveness_Sql_3_Restart_GW()
        {
            await Do_Liveness_OracleTest_2(1);
        }

        [Test, Category("Membership"), Category("SqlServer")]
        public async Task Liveness_Sql_4_Restart_Silo_1()
        {
            await Do_Liveness_OracleTest_2(2);
        }

        [Test, Category("Membership"), Category("SqlServer")]
        public async Task Liveness_Sql_5_Kill_Silo_1_With_Timers()
        {
            await Do_Liveness_OracleTest_2(2, false, true);
        }
    }
}
