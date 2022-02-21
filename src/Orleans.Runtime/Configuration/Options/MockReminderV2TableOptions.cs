
using System;

namespace Orleans.Configuration
{
    /// <summary>
    /// Settings for the mock reminder V2 service.
    /// </summary>
    public class MockReminderV2TableOptions
    {
        /// <summary>
        /// Gets or sets the delay inserted before every operation completes.
        /// </summary>
        public TimeSpan OperationDelay { get; set; } = DEFAULT_MOCK_REMINDER_TABLE_DELAY;

        /// <summary>
        /// The default value for <see cref="MockReminderV2TableOptions"/>.
        /// </summary>
        public static readonly TimeSpan DEFAULT_MOCK_REMINDER_TABLE_DELAY = TimeSpan.FromMilliseconds(50);
    }
}
