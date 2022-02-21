using System.Threading.Tasks;
using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;

namespace DefaultCluster.Tests
{
    public class ReminderV2Test : HostedTestClusterEnsureDefaultStarted
    {
        public ReminderV2Test(DefaultClusterFixture fixture) : base(fixture)
        {
        }

        [Fact, TestCategory("BVT"), TestCategory("Reminders")]
        public async Task SimpleGrainGetGrain()
        {
            IReminderV2TestGrain grain = this.GrainFactory.GetGrain<IReminderV2TestGrain>(GetRandomGrainId());
            bool notExists = await grain.IsReminderExists("not exists");
            Assert.False(notExists);

            await grain.AddReminder("dummy");
            Assert.True(await grain.IsReminderExists("dummy"));

            await grain.RemoveReminder("dummy");
            Assert.False(await grain.IsReminderExists("dummy"));
        }
    }
}