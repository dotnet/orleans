//#define USE_SQL_SERVER
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.TestingHost;
using UnitTests.Tester;

// ReSharper disable InconsistentNaming

namespace UnitTests.LivenessTests
{
    public class Liveness_OracleTests_Base : UnitTestSiloHost
    {
        protected Liveness_OracleTests_Base(TestingSiloOptions siloOptions)
            : base(siloOptions)
        { }

        public TestContext TestContext { get; set; }

        protected async Task Do_Liveness_OracleTest_1()
        {
            Console.WriteLine("DeploymentId= {0} ServiceId = {1}", DeploymentId, ServiceId);

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
            Console.WriteLine("About to reset {0}", address);
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
    }

    [TestClass]
    public class Liveness_OracleTests_MembershipGrain : Liveness_OracleTests_Base
    {
        private static readonly TestingSiloOptions siloOptions = new TestingSiloOptions
        {
            StartFreshOrleans = true,
            StartPrimary = true,
            StartSecondary = true,
            LivenessType = GlobalConfiguration.LivenessProviderType.MembershipTableGrain,
            ReminderServiceType = GlobalConfiguration.ReminderServiceProviderType.ReminderTableGrain
        };

        public Liveness_OracleTests_MembershipGrain()
            : base(siloOptions)
        { }

        [TestCleanup]
        public void TestCleanup()
        {
            Console.WriteLine("Test {0} completed - Outcome = {1}", TestContext.TestName, TestContext.CurrentTestOutcome);
        }

        [ClassCleanup]
        public static void ClassCleanup()
        {
            Console.WriteLine("Class Cleanup");
            StopAdditionalSilos();
            RestartDefaultSilos();
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Liveness")]
        public void Silo_Config_MembershipGrain()
        {
            GlobalConfiguration config = Primary.Silo.GlobalConfig;
            Assert.AreEqual(GlobalConfiguration.LivenessProviderType.MembershipTableGrain, config.LivenessType, "LivenessType");
            Assert.AreEqual(GlobalConfiguration.ReminderServiceProviderType.ReminderTableGrain, config.ReminderServiceType, "ReminderServiceType");
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Liveness")]
        public async Task Liveness_Grain_Oracle()
        {
            await Do_Liveness_OracleTest_1();
        }
    }

    [TestClass]
    public class Liveness_OracleTests_AzureTable : Liveness_OracleTests_Base
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

        public Liveness_OracleTests_AzureTable()
            : base(siloOptions)
        { }

        [TestCleanup]
        public void TestCleanup()
        {
            Console.WriteLine("Test {0} completed - Outcome = {1}", TestContext.TestName, TestContext.CurrentTestOutcome);
        }

        [ClassCleanup]
        public static void ClassCleanup()
        {
            StopAdditionalSilos();
            RestartDefaultSilos();
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Liveness"), TestCategory("Azure")]
        public void Silo_Config_AzureTable()
        {
            GlobalConfiguration config = Primary.Silo.GlobalConfig;
            Assert.AreEqual(GlobalConfiguration.LivenessProviderType.AzureTable, config.LivenessType, "LivenessType");
            Assert.AreEqual(GlobalConfiguration.ReminderServiceProviderType.ReminderTableGrain, config.ReminderServiceType, "ReminderServiceType");
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Liveness"), TestCategory("Azure")]
        public async Task Liveness_Azure_Oracle()
        {
            await Do_Liveness_OracleTest_1();
        }
    }

#if USE_SQL_SERVER || DEBUG
    [TestClass]
    public class Liveness_OracleTests_SqlServer : Liveness_OracleTests_Base
    {
        private static readonly TestingSiloOptions siloOptions = new TestingSiloOptions
        {
            StartFreshOrleans = true,
            StartPrimary = true,
            StartSecondary = true,
            DataConnectionString = "Set-in-ClassInitialize",
            LivenessType = GlobalConfiguration.LivenessProviderType.SqlServer,
            ReminderServiceType = GlobalConfiguration.ReminderServiceProviderType.ReminderTableGrain
        };

        public Liveness_OracleTests_SqlServer()
            : base(siloOptions)
        { }

        [ClassInitialize]
        public static void ClassInitialize(TestContext context)
        {
            Console.WriteLine("TestContext.DeploymentDirectory={0}", context.DeploymentDirectory);
            Console.WriteLine("TestContext=");
            Console.WriteLine(DumpTestContext(context));

            siloOptions.DataConnectionString = StorageTestConstants.GetSqlConnectionString(context.DeploymentDirectory);
        }

        [TestCleanup]
        public void TestCleanup()
        {
            Console.WriteLine("Test {0} completed - Outcome = {1}", TestContext.TestName, TestContext.CurrentTestOutcome);
        }

        [ClassCleanup]
        public static void ClassCleanup()
        {
            StopAdditionalSilos();
            RestartDefaultSilos();
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Liveness"), TestCategory("SqlServer")]
        public void Silo_Config_SqlServer()
        {
            GlobalConfiguration config = Primary.Silo.GlobalConfig;
            Assert.AreEqual(GlobalConfiguration.LivenessProviderType.SqlServer, config.LivenessType, "LivenessType");
            Assert.AreEqual(GlobalConfiguration.ReminderServiceProviderType.ReminderTableGrain, config.ReminderServiceType, "ReminderServiceType");
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Liveness"), TestCategory("SqlServer")]
        public async Task Liveness_Sql_Oracle()
        {
            await Do_Liveness_OracleTest_1();
        }
    }
#endif
}
// ReSharper restore InconsistentNaming
