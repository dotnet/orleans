using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Reminders.Redis;
using Orleans.Runtime;
using Orleans.TestingHost;
using TestExtensions;
using UnitTests;
using UnitTests.GrainInterfaces;
using UnitTests.RemindersTest;
using Xunit;

namespace Tester.Redis.Reminders
{
    public partial class RedisRemindersTableTests
    {
        //protected GrainId MakeTestGrainReference()
        //{
        //    return MakeTestGrainReference(Guid.NewGuid().ToString());
        //}
        //protected GrainId MakeTestGrainReference(string grainId)
        //{
        //    return clusterFixture.Client.GetGrain<IReminderTestGrain>(grainId).GetGrainId();
        //}

        //[Theory]
        //[InlineData("aa:bb")]
        //[InlineData("aa_bb")]
        //public async Task ReminderWithSpecialName(string reminderName)
        //{
        //    await ReminderSimple(MakeTestGrainReference(), reminderName);
        //}

        //[Theory]
        //[InlineData("aa:bb")]
        //[InlineData("aa_bb")]
        //public async Task ReminderWithSpecialGrainId(string grainId)
        //{
        //    await ReminderSimple(MakeTestGrainReference(grainId), "0");
        //}

        //[Fact]
        //public async Task ReadNonExistentReminder()
        //{
        //    ReminderEntry reminder = await remindersTable.ReadRow(MakeTestGrainReference(), "ThereIsNoReminder");
        //    Assert.Null(reminder);
        //}
    }
}
