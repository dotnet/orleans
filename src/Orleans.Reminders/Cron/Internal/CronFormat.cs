#nullable enable
using System;

namespace Orleans.Reminders.Cron.Internal
{
    /// <summary>
    /// Defines the cron format options that customize string parsing for <see cref="CronExpression.Parse(string, CronFormat)"/>.
    /// </summary>
    [Flags]
    internal enum CronFormat
    {
        /// <summary>
        /// Parsing string must contain only 5 fields: minute, hour, day of month, month, day of week.
        /// </summary>
#pragma warning disable CA1008
        Standard = 0,
#pragma warning restore CA1008

        /// <summary>
        /// Second field must be specified in parsing string.
        /// </summary>
        IncludeSeconds = 1
    }
}
