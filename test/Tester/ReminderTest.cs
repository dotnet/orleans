using System.Threading.Tasks;
using UnitTests.GrainInterfaces;
using UnitTests.Tester;
using Xunit;
using Assert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace Tester
{
    public class ReminderTest : HostedTestClusterEnsureDefaultStarted
    {
        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Reminders")]
        public async Task SimpleGrainGetGrain()
        {
            IReminderTestGrain grain = GrainFactory.GetGrain<IReminderTestGrain>(GetRandomGrainId());
            bool notExists = await grain.IsReminderExists("not exists");
            Assert.IsFalse(notExists);
        }
    }
}