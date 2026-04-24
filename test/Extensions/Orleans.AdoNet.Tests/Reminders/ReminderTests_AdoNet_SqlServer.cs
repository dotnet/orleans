#nullable enable

//#define USE_SQL_SERVER

using Orleans.Testing.Reminders;
using Orleans.TestingHost;
using TestExtensions;
using UnitTests.General;
using UnitTests.GrainInterfaces;
using UnitTests.TimerTests;
using Orleans.Tests.SqlUtils;
using Orleans.Internal;
using Xunit;

// ReSharper disable InconsistentNaming
// ReSharper disable UnusedVariable

namespace Tester.AdoNet.Reminders
{
    /// <summary>
    /// Integration tests for Orleans reminders functionality using SQL Server as the reminder service backend.
    /// </summary>
    [TestCategory("Reminders"), TestCategory("AdoNet"), TestCategory("SqlServer")]
    public class ReminderTests_AdoNet_SqlServer : ReminderTestsBase, IClassFixture<ReminderTests_AdoNet_SqlServer.Fixture>
    {
        private const string TestDatabaseName = "OrleansTest_SqlServer_Reminders";
        private static readonly string AdoInvariant = AdoNetInvariants.InvariantNameSqlServer;

        public class Fixture : BaseInProcessTestClusterFixture
        {
            private ReminderTestClock? _reminderClock;
            internal ReminderTestClock ReminderClock => _reminderClock ?? throw new InvalidOperationException($"{nameof(ReminderTestClock)} has not been configured.");

            protected override void CheckPreconditionsOrThrow()
            {
                RelationalStorageForTesting.CheckPreconditionsOrThrow(AdoInvariant);
            }

            protected override void ConfigureTestCluster(InProcessTestClusterBuilder builder)
            {
                string connectionString = RelationalStorageForTesting.SetupInstance(AdoInvariant, TestDatabaseName)
                    .Result.CurrentConnectionString;
                _reminderClock = builder.AddReminderTestClock();
                builder.ConfigureSilo((_, siloBuilder) =>
                {
                    siloBuilder.UseAdoNetReminderService(options =>
                    {
                        options.ConnectionString = connectionString;
                        options.Invariant = AdoInvariant;
                    });
                });
            }

            public override async Task DisposeAsync()
            {
                try
                {
                    await base.DisposeAsync();
                }
                finally
                {
                    _reminderClock?.Dispose();
                }
            }
        }

        public ReminderTests_AdoNet_SqlServer(Fixture fixture) : base(fixture.ReminderClock, fixture.HostedCluster)
        {
            // ReminderTable.Clear() cannot be called from a non-Orleans thread,
            // so we must proxy the call through a grain.
            var controlProxy = GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
            controlProxy.EraseReminderTable().WaitAsync(TestConstants.InitTimeout).Wait();
        }
        
        // Basic tests

        [SkippableFact]
        public async Task Rem_Sql_Basic_StopByRef()
        {
            await Test_Reminders_Basic_StopByRef();
        }

        [SkippableFact]
        public async Task Rem_Sql_UpdateReminder_DoesNotRestartLocalReminder()
        {
            await Test_Reminders_UpdateReminder_DoesNotRestartLocalReminder();
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
