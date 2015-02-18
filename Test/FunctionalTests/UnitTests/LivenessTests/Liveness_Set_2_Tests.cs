//#define USE_SQL_SERVER
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using UnitTestGrainInterfaces;

#pragma warning disable 618

namespace UnitTests.LivenessTests
{
    public abstract class Liveness_Set_2_Base : UnitTestBase
    {
        private const int numAdditionalSilos = 1;
        private const int numGrains = 100;

        protected Liveness_Set_2_Base(Options siloOptions, ClientOptions clientOptions)
            : base(siloOptions, clientOptions)
        { }

        protected void DoTestCleanup()
        {
            Console.WriteLine("Test {0} completed - Outcome = {1}", TestContext.TestName, TestContext.CurrentTestOutcome);

            ResetAllAdditionalRuntimes();
            ResetDefaultRuntimes();
        }

        protected async Task Liveness_Set_2_Runner(int silo2Stop, bool softKill = true, bool startTimers = false)
        {
            List<SiloHandle> additionalSilos = StartAdditionalOrleansRuntimes(numAdditionalSilos);
            WaitForLivenessToStabilize();

            List<ISimpleSelfManagedGrain> grains = new List<ISimpleSelfManagedGrain>();
            for (int i = 0; i < numGrains; i++)
            {
                long key = i + 1;
                ISimpleSelfManagedGrain g1 = SimpleSelfManagedGrainFactory.GetGrain(key);
                grains.Add(g1);
                Assert.AreEqual(key, g1.GetPrimaryKeyLong());
                //Assert.AreEqual(key, g1.GetKey().Result);
                Assert.AreEqual(key.ToString(CultureInfo.InvariantCulture), await g1.GetLabel());
                if (startTimers)
                {
                    await g1.StartTimer();
                }
                await LogGrainIdentity(logger, g1);
            }

            SiloHandle silo2Kill;
            if (silo2Stop == 0)
                silo2Kill = Primary;
            else if (silo2Stop == 1)
                silo2Kill = Secondary;
            else
                silo2Kill = additionalSilos[silo2Stop - 2];

            logger.Info("\n\n\n\nAbout to kill {0}\n\n\n", silo2Kill.Endpoint);

            if (softKill)
                ResetRuntime(silo2Kill);
            else
                KillRuntime(silo2Kill);

            WaitForLivenessToStabilize(softKill);

            logger.Info("\n\n\n\nAbout to start sending msg to grain again\n\n\n");

            for (int i = 0; i < grains.Count; i++)
            {
                long key = i + 1;
                ISimpleSelfManagedGrain g1 = grains[i];
                Assert.AreEqual(key, g1.GetPrimaryKeyLong());
                Assert.AreEqual(key.ToString(CultureInfo.InvariantCulture), await g1.GetLabel());
                await LogGrainIdentity(logger, g1);
            }

            for (int i = numGrains; i < 2 * numGrains; i++)
            {
                long key = i + 1;
                ISimpleSelfManagedGrain g1 = SimpleSelfManagedGrainFactory.GetGrain(key);
                grains.Add(g1);
                Assert.AreEqual(key, g1.GetPrimaryKeyLong());
                Assert.AreEqual(key.ToString(CultureInfo.InvariantCulture), await g1.GetLabel());
                await LogGrainIdentity(logger, g1);
            }
            logger.Info("======================================================");
        }

        private static async Task LogGrainIdentity(TraceLogger logger, ISimpleSelfManagedGrain grain)
        {
            logger.Info("Grain {0}, activation {1} on {2}",
                await grain.GetGrainId(),
                await grain.GetActivationId(),
                await grain.GetRuntimeInstanceId());
        }
    }

    [TestClass]
    public class Liveness_Set_2_MembershipGrain : Liveness_Set_2_Base
    {
        private static readonly Options siloOptions = new Options
        {
            StartFreshOrleans = true,
            StartPrimary = true,
            StartSecondary = true,
            LivenessType = GlobalConfiguration.LivenessProviderType.MembershipTableGrain,
            ReminderServiceType = GlobalConfiguration.ReminderServiceProviderType.ReminderTableGrain
        };
        private static readonly ClientOptions clientOptions = new ClientOptions
        {
            ProxiedGateway = true,
            Gateways = new List<IPEndPoint>(new[]
            {
                new IPEndPoint(IPAddress.Loopback, 30000), 
                new IPEndPoint(IPAddress.Loopback, 30001)
            }),
            PreferedGatewayIndex = 1
        };

        public Liveness_Set_2_MembershipGrain()
            : base(siloOptions, clientOptions)
        { }

        [TestCleanup]
        public void TestCleanup()
        {
            base.DoTestCleanup();
        }

        // Killing Primary is not supported with MembershipTableGrain, because that is the silo where MembershipGrain is running!
        //[TestMethod, TestCategory("Nightly"), TestCategory("Liveness")]
        //public void Liveness_Grain_Set_2_Kill_Primary()
        //{
        //    Liveness_Set_2_Runner(0);
        //}

        [TestMethod, TestCategory("BVT"), TestCategory("Nightly"), TestCategory("Liveness")]
        public async Task Liveness_Grain_Set_2_Kill_GW()
        {
            await Liveness_Set_2_Runner(1);
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("Liveness")]
        public async Task Liveness_Grain_Set_2_Kill_Silo_1()
        {
            await Liveness_Set_2_Runner(2);
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("Liveness")]
        public async Task Liveness_Grain_Set_2_Kill_Silo_1_With_Timers()
        {
            await Liveness_Set_2_Runner(2, false, true);
        }
    }

    [TestClass]
    public class Liveness_Set_2_AzureTable : Liveness_Set_2_Base
    {
        private static readonly Options siloOptions = new Options
        {
            StartFreshOrleans = true,
            StartPrimary = true,
            StartSecondary = true,
            DataConnectionString = TestConstants.DataConnectionString,
            LivenessType = GlobalConfiguration.LivenessProviderType.AzureTable,
            ReminderServiceType = GlobalConfiguration.ReminderServiceProviderType.ReminderTableGrain
        };
        private static readonly ClientOptions clientOptions = new ClientOptions
        {
            ProxiedGateway = true,
            Gateways = new List<IPEndPoint>(new[]
            {
                new IPEndPoint(IPAddress.Loopback, 30000), 
                new IPEndPoint(IPAddress.Loopback, 30001)
            }),
            PreferedGatewayIndex = 1,
        };

        public Liveness_Set_2_AzureTable()
            : base(siloOptions, clientOptions)
        { }

        [TestCleanup]
        public void TestCleanup()
        {
            base.DoTestCleanup();
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("Liveness"), TestCategory("Azure")]
        public async Task Liveness_Azure_Set_2_Kill_Primary()
        {
            await Liveness_Set_2_Runner(0);
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("Liveness"), TestCategory("Azure")]
        public async Task Liveness_Azure_Set_2_Kill_GW()
        {
            await Liveness_Set_2_Runner(1);
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("Liveness"), TestCategory("Azure")]
        public async Task Liveness_Azure_Set_2_Kill_Silo_1()
        {
            await Liveness_Set_2_Runner(2);
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("Liveness"), TestCategory("Azure")]
        public async Task Liveness_Azure_Set_2_Kill_Silo_1_With_Timers()
        {
            await Liveness_Set_2_Runner(2, false, true);
        }
    }

#if USE_SQL_SERVER || DEBUG
    [TestClass]
    public class Liveness_Set_2_SqlServer : Liveness_Set_2_Base
    {
        private static readonly Options siloOptions = new Options
        {
            StartFreshOrleans = true,
            StartPrimary = true,
            StartSecondary = true,
            DataConnectionString = "Set-in-ClassInitialize",
            LivenessType = GlobalConfiguration.LivenessProviderType.SqlServer,
            ReminderServiceType = GlobalConfiguration.ReminderServiceProviderType.ReminderTableGrain
        };
        private static readonly ClientOptions clientOptions = new ClientOptions
        {
            ProxiedGateway = true,
            Gateways = new List<IPEndPoint>(new[]
            {
                new IPEndPoint(IPAddress.Loopback, 30000), 
                new IPEndPoint(IPAddress.Loopback, 30001)
            }),
            PreferedGatewayIndex = 1,
        };

        public Liveness_Set_2_SqlServer()
            : base(siloOptions, clientOptions)
        { }

        [ClassInitialize]
        public static void ClassInitialize(TestContext context)
        {
            Console.WriteLine("TestContext.DeploymentDirectory={0}", context.DeploymentDirectory);
            Console.WriteLine("TestContext=");
            Console.WriteLine(TestConstants.DumpTestContext(context));

            siloOptions.DataConnectionString = TestConstants.GetSqlConnectionString(context);

            ClientConfiguration cfg = ClientConfiguration.StandardLoad();
            TraceLogger.Initialize(cfg);
#if DEBUG
            TraceLogger.AddTraceLevelOverride("Storage", Logger.Severity.Verbose3);
            TraceLogger.AddTraceLevelOverride("Membership", Logger.Severity.Verbose3);
#endif
        }

        [TestCleanup]
        public void TestCleanup()
        {
            base.DoTestCleanup();
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("Liveness"), TestCategory("SqlServer")]
        public async Task Liveness_Sql_Set_2_Kill_Primary()
        {
            await Liveness_Set_2_Runner(0);
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("Liveness"), TestCategory("SqlServer")]
        public async Task Liveness_Sql_Set_2_Kill_GW()
        {
            await Liveness_Set_2_Runner(1);
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("Liveness"), TestCategory("SqlServer")]
        public async Task Liveness_Sql_Set_2_Kill_Silo_1()
        {
            await Liveness_Set_2_Runner(2);
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("Liveness"), TestCategory("SqlServer")]
        public async Task Liveness_Sql_Set_2_Kill_Silo_1_With_Timers()
        {
            await Liveness_Set_2_Runner(2, false, true);
        }
    }
#endif
}

#pragma warning restore 618
