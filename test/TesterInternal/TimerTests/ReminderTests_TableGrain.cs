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
    public class ReminderTests_TableGrain : ReminderTests_Base, IClassFixture<ReminderTests_TableGrain.Fixture>
    {
        public class Fixture : BaseTestClusterFixture
        {
            protected override void ConfigureTestCluster(TestClusterBuilder builder)
            {
                builder.ConfigureLegacyConfiguration();
            }
        }

        public ReminderTests_TableGrain(Fixture fixture) : base(fixture)
        {
            // ReminderTable.Clear() cannot be called from a non-Orleans thread,
            // so we must proxy the call through a grain.
            var controlProxy = this.GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
            controlProxy.EraseReminderTable().WaitWithThrow(TestConstants.InitTimeout);
        }

        // Basic tests

        [Fact, TestCategory("Functional"), TestCategory("ReminderService")]
        public async Task Rem_Grain_Basic_StopByRef()
        {
            await Test_Reminders_Basic_StopByRef();
        }

        [Fact, TestCategory("Functional"), TestCategory("ReminderService")]
        public async Task Rem_Grain_Basic_ListOps()
        {
            await Test_Reminders_Basic_ListOps();
        }

        [Fact, TestCategory("Functional"), TestCategory("ReminderService")]
        public async Task Rem_Grain_MultipleReminders()
        {
            //log.Info(TestContext.TestName);
            IReminderTestGrain2 grain = this.GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
            await PerGrainMultiReminderTest(grain);
        }

        // Single join tests ... multi grain, multi reminders

        [Fact, TestCategory("Functional"), TestCategory("ReminderService")]
        public async Task Rem_Grain_1J_MultiGrainMultiReminders()
        {
            await Test_Reminders_1J_MultiGrainMultiReminders();
        }

        [Fact, TestCategory("Functional"), TestCategory("ReminderService")]
        public async Task Rem_Grain_ReminderNotFounds()
        {
            await Test_Reminders_ReminderNotFound();
        }
    }

}
// ReSharper restore InconsistentNaming
// ReSharper restore UnusedVariable
