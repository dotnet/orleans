//#define USE_SQL_SERVER

using System;
using System.Threading.Tasks;
using Orleans;
using Orleans.Runtime.Configuration;
using Orleans.TestingHost;
using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;
using Tester;

// ReSharper disable InconsistentNaming
// ReSharper disable UnusedVariable

namespace UnitTests.TimerTests
{


#if USE_SQL_SERVER || DEBUG
    public class ReminderTests_SqlServer : ReminderTests_Base, IClassFixture<ReminderTests_SqlServer.Fixture>
    {
        public class Fixture : BaseTestClusterFixture
        {
            protected override TestCluster CreateTestCluster()
            {
                var options = new TestClusterOptions();

                options.ClusterConfiguration.Globals.DataConnectionString = TestHelper.TestUtils.GetSqlConnectionString();
                options.ClusterConfiguration.Globals.ReminderServiceType = GlobalConfiguration.ReminderServiceProviderType.SqlServer;

                return new TestCluster(options);
            }
        }

        public ReminderTests_SqlServer(Fixture fixture) : base(fixture)
        {
            // ReminderTable.Clear() cannot be called from a non-Orleans thread,
            // so we must proxy the call through a grain.
            var controlProxy = fixture.GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
            controlProxy.EraseReminderTable().WaitWithThrow(TestConstants.InitTimeout);
        }
        
        // Basic tests

        [Fact, TestCategory("ReminderService"), TestCategory("SqlServer")]
        public async Task Rem_Sql_Basic_StopByRef()
        {
            await Test_Reminders_Basic_StopByRef();
        }

        [Fact, TestCategory("ReminderService"), TestCategory("SqlServer")]
        public async Task Rem_Sql_Basic_ListOps()
        {
            await Test_Reminders_Basic_ListOps();
        }

        // Single join tests ... multi grain, multi reminders

        [Fact, TestCategory("ReminderService"), TestCategory("SqlServer")]
        public async Task Rem_Sql_1J_MultiGrainMultiReminders()
        {
            await Test_Reminders_1J_MultiGrainMultiReminders();
        }

        [Fact, TestCategory("ReminderService"), TestCategory("SqlServer")]
        public async Task Rem_Sql_ReminderNotFound()
        {
            await Test_Reminders_ReminderNotFound();
        }
    }
#endif

}
// ReSharper restore InconsistentNaming
// ReSharper restore UnusedVariable
