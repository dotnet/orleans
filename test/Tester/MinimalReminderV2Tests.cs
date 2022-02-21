using System;
using System.Threading.Tasks;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.TestingHost;
using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;

namespace UnitTests.CatalogTests
{
    public class MinimalReminderV2Tests : IClassFixture<MinimalReminderTests.Fixture>
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
                siloBuilder.Configure<ReminderV2Options>(options =>
                        options.MinimumReminderPeriod = TimeSpan.FromMilliseconds(100))
                    .UseInMemoryReminderV2Service();
            }
        }

        public MinimalReminderV2Tests(Fixture fixture)
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