//#define USE_SQL_SERVER

using System;
using System.Threading.Tasks;
using Orleans;
using Orleans.Runtime.Configuration;
using Orleans.Tests.SqlUtils;
using Orleans.TestingHost;
using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;
using Tester;
using UnitTests.General;

// ReSharper disable InconsistentNaming
// ReSharper disable UnusedVariable

namespace UnitTests.TimerTests
{


#if USE_SQL_SERVER || DEBUG
    public class ReminderTests_SqlServer : ReminderTests_Base, IClassFixture<ReminderTests_SqlServer.Fixture>
    {
        public class Fixture : BaseTestClusterFixture
        {
            protected override void ConfigureTestCluster(TestClusterBuilder builder)
            {
                string connectionString = RelationalStorageForTesting.SetupInstance(AdoNetInvariants.InvariantNameSqlServer, "OrleansRemiderTestSQL")
                            .Result.CurrentConnectionString;
                builder.ConfigureLegacyConfiguration(legacy =>
                {
                    legacy.ClusterConfiguration.Globals.DataConnectionString = connectionString;
                    legacy.ClusterConfiguration.Globals.ReminderServiceType = GlobalConfiguration.ReminderServiceProviderType.SqlServer;
                });
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
