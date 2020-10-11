using System;
using System.Threading.Tasks;
using Orleans;
using Orleans.Runtime;

namespace Grains
{
    /// <summary>
    /// Demonstrates a grain that performs an internal operation based on a reminder.
    /// </summary>
    public class ReminderGrain : Grain, IReminderGrain, IRemindable
    {
        private int value;

        public override async Task OnActivateAsync()
        {
            await RegisterOrUpdateReminder(nameof(IncrementAsync), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
            await base.OnActivateAsync();
        }

        public Task<int> GetValueAsync() => Task.FromResult(value);

        private Task IncrementAsync()
        {
            value += 1;
            return Task.CompletedTask;
        }

        /// <summary>
        /// Open up the reminder registration method for mocking.
        /// </summary>
        public virtual new Task<IGrainReminder> RegisterOrUpdateReminder(string reminderName, TimeSpan dueTime, TimeSpan period) =>
            base.RegisterOrUpdateReminder(reminderName, dueTime, period);

        public async Task ReceiveReminder(string reminderName, TickStatus status)
        {
            switch (reminderName)
            {
                case nameof(IncrementAsync):
                    await IncrementAsync();
                    break;

                default:
                    await UnregisterReminder(await GetReminder(reminderName));
                    break;
            }
        }
    }
}