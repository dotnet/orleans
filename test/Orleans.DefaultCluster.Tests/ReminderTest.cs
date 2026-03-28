using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;

namespace DefaultCluster.Tests
{
    /// <summary>
    /// Tests for Orleans Reminders functionality.
    /// Reminders are a durable scheduling mechanism that persist across grain
    /// deactivations and cluster restarts. Unlike timers (which are grain-local
    /// and transient), reminders are stored in external storage and will
    /// reliably trigger grain methods at specified intervals, making them
    /// ideal for periodic background tasks that must survive failures.
    /// </summary>
    public class ReminderTest : HostedTestClusterEnsureDefaultStarted
    {
        public ReminderTest(DefaultClusterFixture fixture) : base(fixture)
        {
        }

        /// <summary>
        /// Tests basic reminder operations: checking existence, adding, and removing.
        /// Verifies that:
        /// - Non-existent reminders return false for existence checks
        /// - Reminders can be successfully registered with a grain
        /// - Registered reminders can be detected via existence checks
        /// - Reminders can be removed and no longer exist afterwards
        /// This demonstrates the fundamental CRUD operations for reminders.
        /// </summary>
        [Fact, TestCategory("BVT"), TestCategory("Reminders")]
        public async Task SimpleGrainGetGrain()
        {
            IReminderTestGrain grain = this.GrainFactory.GetGrain<IReminderTestGrain>(GetRandomGrainId());
            bool notExists = await grain.IsReminderExists("not exists");
            Assert.False(notExists);

            await grain.AddReminder("dummy");
            Assert.True(await grain.IsReminderExists("dummy"));

            await grain.RemoveReminder("dummy");
            Assert.False(await grain.IsReminderExists("dummy"));
        }
    }
}