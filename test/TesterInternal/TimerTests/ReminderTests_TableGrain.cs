#nullable enable
//#define USE_SQL_SERVER

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Orleans.TestingHost;
using Orleans.TestingHost.Diagnostics;
using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;
using Orleans.Internal;

// ReSharper disable InconsistentNaming
// ReSharper disable UnusedVariable

namespace UnitTests.TimerTests
{
    /// <summary>
    /// Tests for grain-based reminder functionality using in-memory reminder service as table storage.
    /// </summary>
    [TestCategory("Functional"), TestCategory("Reminders")]
    public class ReminderTests_TableGrain : ReminderTests_Base, IClassFixture<ReminderTests_TableGrain.Fixture>
    {
        public class Fixture : BaseTestClusterFixture
        {
            /// <summary>
            /// Shared FakeTimeProvider instance used by all silos and tests.
            /// This enables virtual time control for fast, deterministic testing.
            /// Static so it can be accessed by the SiloConfigurator.
            /// </summary>
            internal static FakeTimeProvider SharedTimeProvider { get; private set; } = null!;

            /// <summary>
            /// Collector for diagnostic events from Orleans.Reminders.
            /// Used to wait for reminder tick events without polling.
            /// </summary>
            public DiagnosticEventCollector DiagnosticCollector { get; private set; } = null!;

            public override async Task InitializeAsync()
            {
                // Create the shared FakeTimeProvider BEFORE starting the cluster
                SharedTimeProvider = new FakeTimeProvider(DateTimeOffset.UtcNow);
                // Create the diagnostic collector BEFORE starting the cluster
                // so it captures all events from the start
                DiagnosticCollector = new DiagnosticEventCollector("Orleans.Reminders");
                await base.InitializeAsync();
            }

            public override async Task DisposeAsync()
            {
                await base.DisposeAsync();
                DiagnosticCollector?.Dispose();
            }

            protected override void ConfigureTestCluster(TestClusterBuilder builder)
            {
                builder.AddSiloBuilderConfigurator<SiloConfigurator>();
            }

            private class SiloConfigurator : ISiloConfigurator
            {
                public void Configure(ISiloBuilder hostBuilder)
                {
                    hostBuilder.AddMemoryGrainStorageAsDefault()
                        .AddReminders()
                        .UseInMemoryReminderService();

                    // Register the shared FakeTimeProvider
                    hostBuilder.Services.AddSingleton<TimeProvider>(SharedTimeProvider);
                }
            }
        }

        private readonly Fixture _fixture;

        public ReminderTests_TableGrain(Fixture fixture) : base(fixture, Fixture.SharedTimeProvider, fixture.DiagnosticCollector)
        {
            _fixture = fixture;
            // ReminderTable.Clear() cannot be called from a non-Orleans thread,
            // so we must proxy the call through a grain.
            var controlProxy = this.GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
            controlProxy.EraseReminderTable().WaitAsync(TestConstants.InitTimeout).Wait();
        }

        // Basic tests

        /// <summary>
        /// Tests basic reminder operations including stopping reminders by reference.
        /// </summary>
        [Fact]
        public async Task Rem_Grain_Basic_StopByRef()
        {
            await Test_Reminders_Basic_StopByRef();
        }

        /// <summary>
        /// Tests basic reminder list operations including creation and retrieval.
        /// </summary>
        [Fact]
        public async Task Rem_Grain_Basic_ListOps()
        {
            await Test_Reminders_Basic_ListOps();
        }

        /// <summary>
        /// Tests handling of multiple reminders per grain.
        /// </summary>
        [Fact]
        public async Task Rem_Grain_MultipleReminders()
        {
            IReminderTestGrain2 grain = this.GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
            await PerGrainMultiReminderTest(grain);
        }

        // Single join tests ... multi grain, multi reminders

        /// <summary>
        /// Tests single join scenario with multiple grains and multiple reminders.
        /// </summary>
        [SkippableFact]
        public async Task Rem_Grain_1J_MultiGrainMultiReminders()
        {
            await Test_Reminders_1J_MultiGrainMultiReminders();
        }

        /// <summary>
        /// Tests handling of reminder not found scenarios.
        /// </summary>
        [Fact]
        public async Task Rem_Grain_ReminderNotFounds()
        {
            await Test_Reminders_ReminderNotFound();
        }
    }
}
// ReSharper restore InconsistentNaming
// ReSharper restore UnusedVariable
