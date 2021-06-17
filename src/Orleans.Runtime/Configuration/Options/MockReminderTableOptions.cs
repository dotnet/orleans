
using System;

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
}
