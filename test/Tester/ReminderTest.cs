using System.Threading.Tasks;
using UnitTests.GrainInterfaces;
using UnitTests.Tester;
using Xunit;

namespace Tester
{
    public class ReminderTest : HostedTestClusterEnsureDefaultStarted
    {
        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Reminders")]
        public async Task SimpleGrainGetGrain()
        {
            IReminderTestGrain grain = GrainFactory.GetGrain<IReminderTestGrain>(GetRandomGrainId());
            bool notExists = await grain.IsReminderExists("not exists");
            Assert.False(notExists);
        }
    }
}