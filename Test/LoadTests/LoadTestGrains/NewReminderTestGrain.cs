using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans;
using Orleans.Runtime;

using LoadTestGrainInterfaces;
using System.Linq;

namespace LoadTestGrains
{
    public class NewReminderTestGrain :
        Grain,
        INewReminderTestGrain, IRemindable
    {
        private readonly Dictionary<string, NewReminderTestStats> statsTab = 
            new Dictionary<string, NewReminderTestStats>();
        private Logger logger = null;

        public Task EnableLogging()
        {
            if (logger == null)
                logger = GetLogger();
            return Task.FromResult(0);
        }

        public Task DisableLogging()
        {
            logger = null;
            return Task.FromResult(0);
        }

        private void ApplyIfLogging(Action<Logger> log)
        {
            if (logger == null)
                return;
            else
                log(logger);
        }

        public Task ReceiveReminder(string reminderName, TickStatus status)
        {
            var stats = statsTab[reminderName];
            stats.Update(status);
            return TaskDone.Done;
        }

        public Task Reset()
        {
            ApplyIfLogging(l => l.Info("NewReminderTestGrain.Reset()"));
            var set = statsTab.Values.ToArray();
            return 
                Task.WhenAll(
                    set.Select(
                        i =>
                            ResetReminder(i.Name))
                    .ToArray());
        }

        // Cannot use runtime internal class inside Grain code!!!
        //public Task ClearSiloReminderTable()
        //{
        //    return ReminderTable.Clear();
        //}

        public async Task StartReminder(string name, TimeSpan delay, TimeSpan period)
        {
            ApplyIfLogging(l => l.Info("NewReminderTestGrain.StartReminder(): name={0}", name));
            // [mlr][todo] we need to verify that the delay works properly.
#pragma warning disable 612,618
            var handle = await RegisterOrUpdateReminder(name, delay, period);
#pragma warning restore 612,618
            var stats = NewReminderTestStats.NewObject(handle, delay, period);
            statsTab[handle.ReminderName] = stats;
        }

        public async Task StopReminder(string name)
        {
            ApplyIfLogging(
                l => 
                    l.Info(
                        "NewReminderTestGrain.StopReminder(): name={0}",
                        name));
            var stats = statsTab[name];
            if (stats.IsRetired)
            {
                throw new InvalidOperationException(
                    string.Format("Reminder {0} has alreaby been stopped.", name));
            }
            else
            {
#pragma warning disable 612,618
                await UnregisterReminder(stats.Handle);
#pragma warning restore 612,618
                stats.Retire();
            }
        }

        public async Task ResetReminder(string name)
        {
            ApplyIfLogging(
                l => 
                    l.Info(
                        "NewReminderTestGrain.ResetReminder(): name={0}",
                        name));
            var stats = statsTab[name];
            if (!stats.IsRetired)
                await StopReminder(name);
            if (!statsTab.Remove(stats.Name))
                throw new InvalidOperationException("unexpectedly failed to remove the reminder's test data.");
        }

        public Task<NewReminderTestStats> GetTestStats(string name)
        {
            return Task.FromResult(statsTab[name]);
        }

        public override async Task OnActivateAsync()
        {
            ApplyIfLogging(l => l.Info("NewReminderTestGrain.OnActivateAsync()"));

            if (statsTab == null)
                await Reset();
        }

        public override Task OnDeactivateAsync()
        {
            ApplyIfLogging(l => l.Info("NewReminderTestGrain.OnDeactivateAsync()"));
            return Task.FromResult(0);
        }
    }
}
