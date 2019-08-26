using System;
using Orleans.Runtime;

namespace Grains.Tests.Hosted.Fakes
{
    public class FakeReminder : IGrainReminder
    {
        public FakeReminder(string reminderName, TimeSpan dueTime, TimeSpan period)
        {
            ReminderName = reminderName;
            DueTime = dueTime;
            Period = period;
        }

        public string ReminderName { get; }
        public TimeSpan DueTime { get; }
        public TimeSpan Period { get; }
    }
}