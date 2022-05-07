//#define USE_SQL_SERVER

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Orleans.Hosting;
using Orleans.TestingHost;
using TestExtensions;
using UnitTests.General;
using UnitTests.GrainInterfaces;
using UnitTests.TimerTests;
using Orleans.Tests.SqlUtils;
using Orleans.Internal;
using Xunit;
using Microsoft.Extensions.Hosting;

// ReSharper disable InconsistentNaming
// ReSharper disable UnusedVariable

namespace Tester.AdoNet.Reminders
{
    [TestCategory("Reminders"), TestCategory("AdoNet"), TestCategory("SqlServer")]
    public class ReminderTests_AdoNet_SqlServer : ReminderTests_Base, IClassFixture<ReminderTests_AdoNet_SqlServer.Fixture>
    {
        private const string TestDatabaseName = "OrleansTest_SqlServer_Reminders";
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

        public class SiloConfigurator : IHostConfigurator
        {
            public void Configure(IHostBuilder hostBuilder)
            {
                hostBuilder.UseOrleans((ctx, siloBuilder) =>
                {
                    siloBuilder.UseAdoNetReminderService(options =>
                    {
                        options.ConnectionString = hostBuilder.GetConfigurationValue(ConnectionStringKey);
                        options.Invariant = AdoInvariant;
                    });
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

        [SkippableFact]
        public async Task Rem_Sql_Basic_StopByRef()
        {
            await Test_Reminders_Basic_StopByRef();
        }

        [SkippableFact]
        public async Task Rem_Sql_Basic_ListOps()
        {
            await Test_Reminders_Basic_ListOps();
        }

        // Single join tests ... multi grain, multi reminders

        [SkippableFact]
        public async Task Rem_Sql_1J_MultiGrainMultiReminders()
        {
            await Test_Reminders_1J_MultiGrainMultiReminders();
        }

        [SkippableFact]
        public async Task Rem_Sql_ReminderNotFound()
        {
            await Test_Reminders_ReminderNotFound();
        }
    }
}
// ReSharper restore InconsistentNaming
// ReSharper restore UnusedVariable
