//#define USE_SQL_SERVER

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Memory;
using Orleans;
using Orleans.Hosting;
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
    [TestCategory("Functional"), TestCategory("ReminderService"), TestCategory("AdoNet")]
    public class ReminderTests_AdoNet : ReminderTests_Base, IClassFixture<ReminderTests_AdoNet.Fixture>
    {
        public class Fixture : BaseTestClusterFixture
        {
            protected override void ConfigureTestCluster(TestClusterBuilder builder)
            {
                string connectionString = RelationalStorageForTesting.SetupInstance(AdoNetInvariants.InvariantNameSqlServer, "OrleansReminderTestSQL")
                    .Result.CurrentConnectionString;
                builder.ConfigureHostConfiguration(config =>
                    {
                        config.AddInMemoryCollection(new[] {new KeyValuePair<string, string>("ReminderConnectionString", connectionString),});
                    });
                builder.AddSiloBuilderConfigurator<SiloConfigurator>();
            }
        }

        public class SiloConfigurator : ISiloBuilderConfigurator {
            public void Configure(ISiloHostBuilder hostBuilder)
            {
                hostBuilder.UseAdoNetReminderService(options =>
                {
                    options.ConnectionString = hostBuilder.GetConfigurationValue("ReminderConnectionString");
                    options.Invariant = "System.Data.SqlClient";
                });
            }
        }

        public ReminderTests_AdoNet(Fixture fixture) : base(fixture)
        {
            // ReminderTable.Clear() cannot be called from a non-Orleans thread,
            // so we must proxy the call through a grain.
            var controlProxy = fixture.GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
            controlProxy.EraseReminderTable().WaitWithThrow(TestConstants.InitTimeout);
        }
        
        // Basic tests

        [Fact]
        public async Task Rem_Sql_Basic_StopByRef()
        {
            await Test_Reminders_Basic_StopByRef();
        }

        [Fact]
        public async Task Rem_Sql_Basic_ListOps()
        {
            await Test_Reminders_Basic_ListOps();
        }

        // Single join tests ... multi grain, multi reminders

        [Fact]
        public async Task Rem_Sql_1J_MultiGrainMultiReminders()
        {
            await Test_Reminders_1J_MultiGrainMultiReminders();
        }

        [Fact]
        public async Task Rem_Sql_ReminderNotFound()
        {
            await Test_Reminders_ReminderNotFound();
        }
    }
}
// ReSharper restore InconsistentNaming
// ReSharper restore UnusedVariable
