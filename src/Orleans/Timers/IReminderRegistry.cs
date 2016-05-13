﻿using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans.Runtime;

namespace Orleans.Timers
{
    public interface IReminderRegistry
    {
        Task<IGrainReminder> RegisterOrUpdateReminder(string reminderName, TimeSpan dueTime, TimeSpan period);

        Task UnregisterReminder(IGrainReminder reminder);

        Task<IGrainReminder> GetReminder(string reminderName);

        Task<List<IGrainReminder>> GetReminders();
    }
}