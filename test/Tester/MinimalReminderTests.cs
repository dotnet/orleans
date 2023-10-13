using Orleans.TestingHost;
using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;

namespace UnitTests.CatalogTests
{
    public class MinimalReminderTests : IClassFixture<MinimalReminderTests.Fixture>
    {
        private readonly Fixture fixture;

        public class Fixture : BaseTestClusterFixture
        {
            protected override void ConfigureTestCluster(TestClusterBuilder builder)
            {
                builder.AddSiloBuilderConfigurator<SiloConfiguration>();
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

        [Fact, TestCategory("Catalog"), TestCategory("Functional")]
        public async Task MinimalReminderInterval()
        {
            var grainGuid = Guid.NewGuid();
            const string reminderName = "minimal_reminder";

            var reminderGrain = this.fixture.GrainFactory.GetGrain<IReminderTestGrain2>(grainGuid);
            _ = await reminderGrain.StartReminder(reminderName, TimeSpan.FromMilliseconds(100), true);

            var r = await reminderGrain.GetReminderObject(reminderName);
            await reminderGrain.StopReminder(r);

        }
    }
}