using Orleans.Internal;
using Orleans.Testing.Reminders;
using Orleans.TestingHost;
using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;

namespace UnitTests.CatalogTests
{
    /// <summary>
    /// Tests reminder functionality with minimal interval configuration (100ms) using in-memory reminder service.
    /// </summary>
    public class MinimalReminderTests : IClassFixture<MinimalReminderTests.Fixture>
    {
        private readonly Fixture fixture;

        public class Fixture : BaseTestClusterFixture
        {
            public ReminderDiagnosticObserver ReminderObserver { get; private set; } = null!;

            protected override void ConfigureTestCluster(TestClusterBuilder builder)
            {
                ReminderObserver = ReminderDiagnosticObserver.Create(builder);
                builder.AddSiloBuilderConfigurator<SiloConfiguration>();
            }

            public override async ValueTask DisposeAsync()
            {
                try
                {
                    using var cleanupCancellation = new CancellationTokenSource(TestConstants.InitTimeout);
                    await base.DisposeAsync().AsTask().WaitAsync(cleanupCancellation.Token);
                }
                finally
                {
                    ReminderObserver?.Dispose();
                }
            }
        }

        public class SiloConfiguration : ISiloConfigurator
        {
            public void Configure(ISiloBuilder siloBuilder)
            {
                siloBuilder.Configure<ReminderOptions>(options =>
                        options.MinimumReminderPeriod = TimeSpan.FromMilliseconds(100))
                    .UseInMemoryReminderService();
            }
        }

        public MinimalReminderTests(Fixture fixture)
        {
            this.fixture = fixture;
        }

        [TestSuite("Functional")]
        [TestProvider("None")]
        [Fact, TestCategory("Catalog"), TestCategory("Functional")]
        public async Task MinimalReminderInterval()
        {
            var grainGuid = Guid.NewGuid();
            const string reminderName = "minimal_reminder";
            var period = TimeSpan.FromMilliseconds(100);

            var reminderGrain = this.fixture.GrainFactory.GetGrain<IReminderTestGrain2>(grainGuid);
            var grainId = reminderGrain.GetGrainId();
            var observer = this.fixture.ReminderObserver;

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            cts.CancelAfter(TestConstants.InitTimeout);
            foreach (var silo in this.fixture.HostedCluster.Silos)
            {
                await observer.WaitForReminderServiceStartedAsync(cts.Token, silo.SiloAddress);
            }

            var registeredTask = observer.WaitForReminderRegisteredAsync(grainId, reminderName, cts.Token);
            var reminder = await reminderGrain.StartReminder(reminderName, period, true).WaitAsync(cts.Token);
            await registeredTask;
            await observer.WaitForLocalReminderScheduleAsync(grainId, reminderName, cts.Token);

            var unregisteredTask = observer.WaitForReminderUnregisteredAsync(grainId, reminderName, cts.Token);
            await reminderGrain.StopReminder(reminder).WaitAsync(cts.Token);
            await unregisteredTask;
            await observer.WaitForReminderQuiescenceAsync(grainId, reminderName, cts.Token);
        }
    }
}
