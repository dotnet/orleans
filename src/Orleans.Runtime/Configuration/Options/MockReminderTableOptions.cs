
using System;

namespace Orleans.Configuration
{
    /// <summary>
    /// Settings for the mock reminder service.
    /// </summary>
    public class MockReminderTableOptions
    {
        /// <summary>
        /// Gets or sets the delay inserted before every operation completes.
        /// </summary>
        public TimeSpan OperationDelay { get; set; } = DEFAULT_MOCK_REMINDER_TABLE_DELAY;

        /// <summary>
        /// The default value for <see cref="MockReminderTableOptions"/>.
        /// </summary>
        public static readonly TimeSpan DEFAULT_MOCK_REMINDER_TABLE_DELAY = TimeSpan.FromMilliseconds(50);
    }
}
