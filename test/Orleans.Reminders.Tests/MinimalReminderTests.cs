using Orleans.Runtime;
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
            var period = TimeSpan.FromMilliseconds(100);

            var reminderGrain = this.fixture.GrainFactory.GetGrain<IReminderTestGrain2>(grainGuid);
            _ = await WaitForReminderServiceReadiness(() => reminderGrain.StartReminder(reminderName, period, true));

            var r = await WaitForReminder(reminderGrain, reminderName);
            await WaitForReminderServiceReadiness(() => reminderGrain.StopReminder(r));

        }

        private static async Task<IGrainReminder> WaitForReminder(IReminderTestGrain2 reminderGrain, string reminderName)
        {
            var deadline = DateTime.UtcNow + TestConstants.InitTimeout;
            Exception lastException = null;

            while (true)
            {
                var remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    throw new TimeoutException($"Timed out waiting for reminder {reminderName} to be readable.", lastException);
                }

                try
                {
                    var reminder = await reminderGrain.GetReminderObject(reminderName).WaitAsync(remaining);
                    if (reminder is not null)
                    {
                        return reminder;
                    }
                }
                catch (OrleansException exception) when (IsReminderServiceInitializing(exception) && DateTime.UtcNow < deadline)
                {
                    lastException = exception;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(100));
            }
        }

        private static async Task<T> WaitForReminderServiceReadiness<T>(Func<Task<T>> operation)
        {
            return await WaitUntilSuccess(operation);
        }

        private static Task WaitForReminderServiceReadiness(Func<Task> operation)
        {
            return WaitUntilSuccess(async () =>
            {
                await operation();
                return true;
            });
        }

        private static async Task<T> WaitUntilSuccess<T>(Func<Task<T>> operation)
        {
            var deadline = DateTime.UtcNow + TestConstants.InitTimeout;
            Exception lastException = null;

            while (true)
            {
                var remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    throw new TimeoutException("Timed out waiting for the reminder operation to complete.", lastException);
                }

                try
                {
                    return await operation().WaitAsync(remaining);
                }
                catch (OrleansException exception) when (IsReminderServiceInitializing(exception) && DateTime.UtcNow < deadline)
                {
                    lastException = exception;
                    await Task.Delay(TimeSpan.FromMilliseconds(100));
                }
            }
        }

        private static bool IsReminderServiceInitializing(Exception exception)
        {
            return exception is OrleansException { Message: { } message }
                && message.Contains("Reminder Service is still initializing", StringComparison.Ordinal);
        }
    }
}
