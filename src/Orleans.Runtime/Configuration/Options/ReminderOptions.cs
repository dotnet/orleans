
using System;
using System.Collections.Generic;
using Microsoft.Extensions.Options;

namespace Orleans.Hosting
{
    /// <summary>
    /// Settings for reminder service.
    /// </summary>
    public class ReminderOptions
    {
        public static class BuiltIn
        {
            public const string NotSpecified = "NotSpecified";

            /// <summary>Grain is used to store reminders information. 
            /// This option is not reliable and thus should only be used in local development setting.</summary>
            public const string ReminderTableGrain = "ReminderTableGrain";

            /// <summary>AzureTable is used to store reminders information. 
            /// This option can be used in production.</summary>
            public const string AzureTable = "AzureTable";

            /// <summary>SQL Server is used to store reminders information. 
            /// This option can be used in production.</summary>
            public const string SqlServer = "SqlServer";

            /// <summary>Used for benchmarking; it simply delays for a specified delay during each operation.</summary>
            public const string MockTable = "MockTable";

            /// <summary>Reminder Service is disabled.</summary>
            public const string Disabled = "Disabled";

            /// <summary>Use custom Reminder Service from third-party assembly</summary>
            public const string Custom = "Custom";
        }

        /// <summary>
        /// The ReminderService attribute controls the type of the reminder service implementation used by silos.
        /// </summary>
        public string ReminderService { get; set; }

        /// <summary>
        /// Assembly to use for custom ReminderTable implementation
        /// </summary>
        public string ReminderTableAssembly { get; set; }

        #region TEST
        /// <summary>
        /// For TEST - Not for use in production environments.
        /// </summary>
        public bool UseMockReminderTable { get; set; } = false;
        /// <summary>
        /// For TEST - Not for use in production environments.
        /// </summary>
        public TimeSpan MockReminderTableTimeout { get; set; } = DEFAULT_MOCK_REMINDER_TABLE_TIMEOUT;
        public static readonly TimeSpan DEFAULT_MOCK_REMINDER_TABLE_TIMEOUT = TimeSpan.FromMilliseconds(50);
        #endregion TEST
    }

    public class ReminderOptionsFormatter : IOptionFormatter<ReminderOptions>
    {
        public string Category { get; }

        public string Name => nameof(ReminderOptions);

        private ReminderOptions options;
        public ReminderOptionsFormatter(IOptions<ReminderOptions> options)
        {
            this.options = options.Value;
        }

        public IEnumerable<string> Format()
        {
            return new List<string>()
            {
                OptionFormattingUtilities.Format(nameof(this.options.ReminderService),this.options.ReminderService),
                OptionFormattingUtilities.Format(nameof(this.options.ReminderTableAssembly), this.options.ReminderTableAssembly),
                OptionFormattingUtilities.Format(nameof(this.options.UseMockReminderTable), this.options.UseMockReminderTable),
                OptionFormattingUtilities.Format(nameof(this.options.MockReminderTableTimeout), this.options.MockReminderTableTimeout),
            };
        }
    }
}
