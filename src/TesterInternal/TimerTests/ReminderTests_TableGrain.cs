//#define USE_SQL_SERVER

using System;
using System.Threading.Tasks;
using Assert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;
using Orleans;
using Orleans.Runtime.Configuration;
using Orleans.TestingHost;
using UnitTests.GrainInterfaces;
using Tester;
using Xunit;

// ReSharper disable InconsistentNaming
// ReSharper disable UnusedVariable

namespace UnitTests.TimerTests
{
    public class ReminderTests_TableGrainFixture : BaseClusterFixture
    {
        public ReminderTests_TableGrainFixture() : base(new TestingSiloHost(new TestingSiloOptions
        {
            StartFreshOrleans = true,
            ReminderServiceType = GlobalConfiguration.ReminderServiceProviderType.ReminderTableGrain,
            LivenessType = GlobalConfiguration.LivenessProviderType.MembershipTableGrain, // Seperate testing of Reminders storage from membership storage
        }))
        {

        }
    }

    public class ReminderTests_TableGrain : ReminderTests_Base, IClassFixture<ReminderTests_TableGrainFixture>, IDisposable
    {
        public ReminderTests_TableGrain(ReminderTests_TableGrainFixture fixture) : base(fixture)
        {
            DoTestInitialize();

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
            IReminderTestGrain2 grain = GrainClient.GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
            await PerGrainMultiReminderTest(grain);
        }

        // Single join tests ... multi grain, multi reminders

        [Fact, TestCategory("Functional"), TestCategory("ReminderService")]
        public async Task Rem_Grain_1J_MultiGrainMultiReminders()
        {
            await Test_Reminders_1J_MultiGrainMultiReminders();
        }
    }

}
// ReSharper restore InconsistentNaming
// ReSharper restore UnusedVariable
