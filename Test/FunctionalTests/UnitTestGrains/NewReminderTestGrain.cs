using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans;
using Orleans.Runtime;
using Orleans.Runtime.ReminderService;

using UnitTestGrainInterfaces;
using System.Linq;

#pragma warning disable 0618

namespace UnitTestGrains
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

        private void ApplyIfLogging(Action<Logger> logAction)
        {
            if (logger != null) logAction(logger);
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

        public Task ClearSiloReminderTable()
        {
            return ReminderTable.TestOnlyClearTable();
        }

        public async Task StartReminder(string name, TimeSpan delay, TimeSpan period)
        {
            ApplyIfLogging(l => l.Info("NewReminderTestGrain.StartReminder(): name={0}", name));
            // we need to verify that the delay works properly.
            var handle = await RegisterOrUpdateReminder(name, delay, period);
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
                await UnregisterReminder(stats.Handle);
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

        public Task<int> GetActiveReminderCount()
        {
            return Task.FromResult(statsTab.Count);
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
