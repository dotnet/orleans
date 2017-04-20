using System.Threading.Tasks;
using Orleans;
using UnitTests.GrainInterfaces;

namespace UnitTests.Grains
{
    internal class ReminderTestGrain : Grain, IReminderTestGrain
    {
        public async Task<bool> IsReminderExists(string reminderName)
        {
            var reminder = await this.GetReminder(reminderName);
            return reminder != null;
        }
    }
}