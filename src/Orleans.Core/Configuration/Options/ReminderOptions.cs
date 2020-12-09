using System;
using Orleans.Runtime;

namespace Orleans.Configuration
{
    public class ReminderOptions
    {
        /// <summary>
        /// Minimal Interval for Reminders. High-frequency reminders are dangerous for production systems.
        /// </summary>
        public TimeSpan MinimalReminderInterval { get; set; } = Constants.MinReminderPeriod;
    }
}