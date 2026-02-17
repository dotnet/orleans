//#define USE_SQL_SERVER

using Orleans.TestingHost;
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
                }
            }
        }

        public ReminderTests_TableGrain(Fixture fixture) : base(fixture)
        {
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
        [Fact(Skip = "https://github.com/dotnet/orleans/issues/9555")]
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
        [SkippableFact(Skip = "https://github.com/dotnet/orleans/issues/4318")]
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
