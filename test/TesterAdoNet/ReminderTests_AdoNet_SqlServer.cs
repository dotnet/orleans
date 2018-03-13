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
    [TestCategory("Functional"), TestCategory("ReminderService"), TestCategory("AdoNet"), TestCategory("SqlServer")]
    public class ReminderTests_AdoNet_SqlServer : ReminderTests_Base, IClassFixture<ReminderTests_AdoNet_SqlServer.Fixture>
    {
        private const string TestDatabaseName = "OrleansTest";
        private static string AdoInvariant = AdoNetInvariants.InvariantNameSqlServer;
        private const string ConnectionStringKey = "ReminderConnectionString";

        public class Fixture : BaseTestClusterFixture
        {
            protected override void ConfigureTestCluster(TestClusterBuilder builder)
            {
                string connectionString = RelationalStorageForTesting.SetupInstance(AdoInvariant, TestDatabaseName)
                    .Result.CurrentConnectionString;
                builder.ConfigureHostConfiguration(config => config.AddInMemoryCollection(new Dictionary<string, string>
                {
                    [ConnectionStringKey] = connectionString
                }));
                builder.AddSiloBuilderConfigurator<SiloConfigurator>();
            }
        }

        public class SiloConfigurator : ISiloBuilderConfigurator
        {
            public void Configure(ISiloHostBuilder hostBuilder)
            {
                hostBuilder.UseAdoNetReminderService(options =>
                {
                    options.ConnectionString = hostBuilder.GetConfigurationValue(ConnectionStringKey);
                    options.Invariant = AdoInvariant;
                });
            }
        }

        public ReminderTests_AdoNet_SqlServer(Fixture fixture) : base(fixture)
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
