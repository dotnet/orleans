using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using Orleans;

namespace LoadTestGrainInterfaces
{
    public interface IReminderLoadTestGrain : IRemindable
    {
        Task Noop();
        Task RegisterReminder(string reminderName);
        Task UnregisterReminder(string reminderName);
    }
}
