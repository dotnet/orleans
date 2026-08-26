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
    [TestSuite("Functional")]
    [TestProvider("SqlServer")]
    [TestArea("Reminders")]
    [TestCategory("Reminders"), TestCategory("AdoNet"), TestCategory("SqlServer")]
    public class ReminderTests_AdoNet_SqlServer : ReminderTestsBase, IClassFixture<ReminderTests_AdoNet_SqlServer.Fixture>, IAsyncLifetime
    {
        private const string TestDatabaseName = "OrleansTest_SqlServer_Reminders";
        private static readonly string AdoInvariant = AdoNetInvariants.InvariantNameSqlServer;

        public class Fixture : BaseInProcessTestClusterFixture
        {
            private string _connectionString = null!;
            private ReminderTestClock? _reminderClock;
            internal ReminderTestClock ReminderClock
            {
                get
                {
                    EnsurePreconditionsMet();
                    return _reminderClock ?? throw new InvalidOperationException($"{nameof(ReminderTestClock)} has not been configured.");
                }
            }

            protected override void CheckPreconditionsOrThrow()
            {
                RelationalStorageForTesting.CheckPreconditionsOrThrow(AdoInvariant);
            }

            public override async ValueTask InitializeAsync()
            {
                if (!PreconditionsMet)
                {
                    return;
                }

                var relationalStorage = await RelationalStorageForTesting.SetupInstance(
                    AdoInvariant,
                    TestDatabaseName,
                    cancellationToken: TestContext.Current.CancellationToken);
                _connectionString = relationalStorage.CurrentConnectionString;
                await base.InitializeAsync();
                if (!PreconditionsMet)
                {
                    return;
                }
            }

            protected override void ConfigureTestCluster(InProcessTestClusterBuilder builder)
            {
                _reminderClock = builder.AddReminderTestClock();
                builder.ConfigureSilo((_, siloBuilder) =>
                {
                    siloBuilder.UseAdoNetReminderService(options =>
                    {
                        options.ConnectionString = _connectionString;
                        options.Invariant = AdoInvariant;
                    });
                });
            }

            public override async ValueTask DisposeAsync()
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
        }

        public async ValueTask InitializeAsync()
        {
            // ReminderTable.Clear() cannot be called from a non-Orleans thread,
            // so we must proxy the call through a grain.
            var controlProxy = GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
            await controlProxy.EraseReminderTable().WaitAsync(TestConstants.InitTimeout, TestContext.Current.CancellationToken);
        }

        ValueTask IAsyncDisposable.DisposeAsync() => ValueTask.CompletedTask;
        
        // Basic tests

        [Fact]
        public async Task Rem_Sql_Basic_StopByRef()
        {
            await Test_Reminders_Basic_StopByRef();
        }

        [Fact]
        public async Task Rem_Sql_UpdateReminder_DoesNotRestartLocalReminder()
        {
            await Test_Reminders_UpdateReminder_DoesNotRestartLocalReminder(TestContext.Current.CancellationToken);
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
