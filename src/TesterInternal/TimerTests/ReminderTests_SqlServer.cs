//#define USE_SQL_SERVER

using System;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans;
using Orleans.Runtime.Configuration;
using Orleans.TestingHost;
using UnitTests.GrainInterfaces;
using UnitTests.TestHelper;

// ReSharper disable InconsistentNaming
// ReSharper disable UnusedVariable

namespace UnitTests.TimerTests
{

#if USE_SQL_SERVER || DEBUG
    [TestClass]
    public class ReminderTests_SqlServer : ReminderTests_Base
    {
        private static readonly TestingSiloOptions siloOptions = new TestingSiloOptions
        {
            StartFreshOrleans = true,
            DataConnectionString = "Set-in-ClassInitialize",
            ReminderServiceType = GlobalConfiguration.ReminderServiceProviderType.SqlServer,
            LivenessType = GlobalConfiguration.LivenessProviderType.MembershipTableGrain, // Seperate testing of Reminders storage from membership storage
        };

        public ReminderTests_SqlServer()
            : base(siloOptions)
        {
        }

        [ClassInitialize]
        public static void ClassInitialize(TestContext context)
        {
            DoClassInitialize();

            Console.WriteLine("TestContext.DeploymentDirectory={0}", context.DeploymentDirectory);
            Console.WriteLine("TestContext=");
            Console.WriteLine(DumpTestContext(context));

            siloOptions.DataConnectionString = TestUtils.GetSqlConnectionString(context);
        }

        [ClassCleanup]
        public static void ClassCleanup()
        {
            DoClassCleanup();
        }

        [TestInitialize]
        public void TestInitialize()
        {
            Console.WriteLine("{0} TestInitialize {1}", GetType().Name, TestContext.TestName);

            // ReminderTable.Clear() cannot be called from a non-Orleans thread,
            // so we must proxy the call through a grain.
            var controlProxy = GrainClient.GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
            controlProxy.EraseReminderTable().WaitWithThrow(TestConstants.InitTimeout);
        }

        [TestCleanup]
        public void TestCleanup()
        {
            DoCleanup();
        }

        // Basic tests

        [TestMethod, TestCategory("ReminderService"), TestCategory("SqlServer")]
        public async Task Rem_Sql_Basic_StopByRef()
        {
            await Test_Reminders_Basic_StopByRef();
        }

        [TestMethod, TestCategory("ReminderService"), TestCategory("SqlServer")]
        public async Task Rem_Sql_Basic_ListOps()
        {
            await Test_Reminders_Basic_ListOps();
        }

        // Single join tests ... multi grain, multi reminders

        [TestMethod, TestCategory("ReminderService"), TestCategory("SqlServer")]
        public async Task Rem_Sql_1J_MultiGrainMultiReminders()
        {
            await Test_Reminders_1J_MultiGrainMultiReminders();
        }
    }
#endif

}
// ReSharper restore InconsistentNaming
// ReSharper restore UnusedVariable
