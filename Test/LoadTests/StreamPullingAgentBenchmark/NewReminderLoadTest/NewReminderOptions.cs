using CommandLine;
using StreamPullingAgentBenchmark.EmbeddedSiloLoadTest;

// ReSharper disable once CheckNamespace
namespace NewReminderLoadTest
{
    public class NewReminderOptions : BaseOptions
    {
        [Option("reminders-per-second", DefaultValue = 15, HelpText = "number of reminders to register per second")]
        public int RemindersPerSecond { get; set; }

        [Option("reminder-period", DefaultValue = 60, HelpText = "reminder period, in seconds")]
        public int ReminderPeriod { get; set; }

        [Option("reminder-duration", DefaultValue = 90, HelpText = "how long to let a reminder to run before unregistering it, in seconds")]
        public int ReminderDuration { get; set; }

        [Option("concurrent-requests", DefaultValue = 6, HelpText = "maximum number of currently running reminder operation tasks")]
        public int ConcurrentRequests { get; set; }

        [Option("skip-get", DefaultValue = true, HelpText = "whether to skip the GetReminder call before calling RegisterOrUpdateReminder")]
        public bool SkipGet { get; set; }
    }
}