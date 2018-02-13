
using System;
using System.Collections.Generic;
using Microsoft.Extensions.Options;

namespace Orleans.Configuration
{
    /// <summary>
    /// Settings for the mock reminder service.
    /// </summary>
    public class MockReminderTableOptions
    {
        /// <summary>
        /// The delay inserted before every operation completes.
        /// </summary>
        public TimeSpan OperationDelay { get; set; } = DEFAULT_MOCK_REMINDER_TABLE_DELAY;
        public static readonly TimeSpan DEFAULT_MOCK_REMINDER_TABLE_DELAY = TimeSpan.FromMilliseconds(50);
    }

    public class MockReminderTableOptionsFormatter : IOptionFormatter<MockReminderTableOptions>
    {
        private readonly MockReminderTableOptions options;

        public string Category { get; }

        public string Name => nameof(MockReminderTableOptions);
        
        public MockReminderTableOptionsFormatter(IOptions<MockReminderTableOptions> options)
        {
            this.options = options.Value;
        }

        public IEnumerable<string> Format()
        {
            return new List<string>()
            {
                OptionFormattingUtilities.Format(nameof(this.options.OperationDelay), this.options.OperationDelay),
            };
        }
    }
}
