using System;
using Orleans.Hosting;

namespace Orleans;

internal static class ReminderOptionsDefaults
{
    /// <summary>
    /// Minimum period for registering a reminder ... we want to enforce a lower bound <see cref="ReminderOptions.MinimumReminderPeriod"/>.
    /// </summary>
    /// <remarks>Increase this period, reminders are supposed to be less frequent ... we use 2 seconds just to reduce the running time of the unit tests</remarks>
    public const uint MinimumReminderPeriodMinutes = 1;

    /// <summary>
    /// Period (in minutes) between refreshing local reminder list to reflect the global reminder table every <see cref="ReminderOptions.RefreshReminderListPeriod"/>.
    /// </summary>
    public const uint RefreshReminderListPeriodMinutes = 5;

    /// <summary>
    /// The maximum amount of time (in minutes) to attempt to initialize reminders giving up <see cref="ReminderOptions.InitializationTimeout"/>.
    /// </summary>
    public const uint InitializationTimeoutMinutes = 5;
}