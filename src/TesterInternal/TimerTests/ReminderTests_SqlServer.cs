//#define USE_SQL_SERVER

using System;
using System.Threading.Tasks;
using Assert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;
using Orleans;
using Orleans.Runtime.Configuration;
using Orleans.TestingHost;
using Tester;
using UnitTests.GrainInterfaces;
using UnitTests.TestHelper;
using TestUtils = Tester.TestUtils;
using Xunit;

// ReSharper disable InconsistentNaming
// ReSharper disable UnusedVariable

namespace UnitTests.TimerTests
{
    public class ReminderTests_SqlServerFixture : BaseClusterFixture
    {
        public ReminderTests_SqlServerFixture() : base(new TestingSiloHost(new TestingSiloOptions
        {
            StartFreshOrleans = true,
            DataConnectionString = TestHelper.TestUtils.GetSqlConnectionString(),
            ReminderServiceType = GlobalConfiguration.ReminderServiceProviderType.SqlServer,
            LivenessType = GlobalConfiguration.LivenessProviderType.MembershipTableGrain, // Seperate testing of Reminders storage from membership storage
        }))
        {

        }
    }

#if USE_SQL_SERVER || DEBUG
    public class ReminderTests_SqlServer : ReminderTests_Base, IClassFixture<ReminderTests_SqlServerFixture>, IDisposable
    {  
        public ReminderTests_SqlServer(ReminderTests_SqlServerFixture fixture) : base(fixture)
        {
            this.DoTestInitialize();

            // ReminderTable.Clear() cannot be called from a non-Orleans thread,
            // so we must proxy the call through a grain.
            var controlProxy = GrainClient.GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
            controlProxy.EraseReminderTable().WaitWithThrow(TestConstants.InitTimeout);
        }
        
        public void Dispose()
        {
            DoTestCleanup();
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
    }
#endif

}
// ReSharper restore InconsistentNaming
// ReSharper restore UnusedVariable
