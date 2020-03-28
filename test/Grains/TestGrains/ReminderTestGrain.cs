using System.Threading.Tasks;
using Orleans;
using Orleans.Timers;
using UnitTests.GrainInterfaces;

namespace UnitTests.Grains
{
    internal class ReminderTestGrain : IReminderTestGrain
    {
        private readonly IReminderRegistry reminderRegistry;

        public ReminderTestGrain(IReminderRegistry reminderRegistry)
        {
            this.reminderRegistry = reminderRegistry;
        }
        public async Task<bool> IsReminderExists(string reminderName)
        {
            var reminder = await reminderRegistry.GetReminder(reminderName);
            return reminder != null;
        }
    }
}