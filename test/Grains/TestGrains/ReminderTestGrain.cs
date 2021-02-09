using System;
using System.Threading.Tasks;
using Orleans;
using UnitTests.GrainInterfaces;

namespace UnitTests.Grains
{
    internal class ReminderTestGrain : Grain, IReminderTestGrain, IRemindable
    {
        public async Task<bool> IsReminderExists(string reminderName)
        {
            var reminder = await this.GetReminder(reminderName);
            return reminder != null;
        }

        public Task AddReminder(string reminderName) => RegisterOrUpdateReminder(reminderName, TimeSpan.FromDays(1), TimeSpan.FromDays(1));

        public async Task RemoveReminder(string reminderName)
        {
            var r = await GetReminder(reminderName) ?? throw new Exception("Reminder not found");
            await UnregisterReminder(r);
        }

        public Task ReceiveReminder(string reminderName, Orleans.Runtime.TickStatus status) => throw new NotSupportedException();
    }
}