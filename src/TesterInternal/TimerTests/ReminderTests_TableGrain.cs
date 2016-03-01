﻿//#define USE_SQL_SERVER

using System;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans;
using Orleans.Runtime.Configuration;
using Orleans.TestingHost;
using UnitTests.GrainInterfaces;

// ReSharper disable InconsistentNaming
// ReSharper disable UnusedVariable

namespace UnitTests.TimerTests
{
    [TestClass]
    public class ReminderTests_TableGrain : ReminderTests_Base
    {
        private static readonly TestingSiloOptions siloOptions = new TestingSiloOptions
        {
            StartFreshOrleans = true,
            ReminderServiceType = GlobalConfiguration.ReminderServiceProviderType.ReminderTableGrain,
            LivenessType = GlobalConfiguration.LivenessProviderType.MembershipTableGrain, // Seperate testing of Reminders storage from membership storage
        };

        public static TestingSiloHost CreateSiloHost()
        {
            return new TestingSiloHost(siloOptions);
        }

        [TestInitialize]
        public void TestInitialize()
        {
            DoTestInitialize();

            // ReminderTable.Clear() cannot be called from a non-Orleans thread,
            // so we must proxy the call through a grain.
            var controlProxy = GrainClient.GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
            controlProxy.EraseReminderTable().WaitWithThrow(TestConstants.InitTimeout);
        }

        [TestCleanup]
        public void TestCleanup()
        {
            DoTestCleanup();
        }

        // Basic tests

        [TestMethod, TestCategory("Functional"), TestCategory("ReminderService")]
        public async Task Rem_Grain_Basic_StopByRef()
        {
            await Test_Reminders_Basic_StopByRef();
        }

        [TestMethod, TestCategory("Functional"), TestCategory("ReminderService")]
        public async Task Rem_Grain_Basic_ListOps()
        {
            await Test_Reminders_Basic_ListOps();
        }

        [TestMethod, TestCategory("Functional"), TestCategory("ReminderService")]
        public async Task Rem_Grain_MultipleReminders()
        {
            log.Info(TestContext.TestName);
            IReminderTestGrain2 grain = GrainClient.GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
            await PerGrainMultiReminderTest(grain);
        }

        // Single join tests ... multi grain, multi reminders

        [TestMethod, TestCategory("Functional"), TestCategory("ReminderService")]
        public async Task Rem_Grain_1J_MultiGrainMultiReminders()
        {
            await Test_Reminders_1J_MultiGrainMultiReminders();
        }
    }

}
// ReSharper restore InconsistentNaming
// ReSharper restore UnusedVariable
