using Orleans.TestingHost;

namespace Orleans.Testing.Reminders;

/// <summary>
/// Extension methods for enabling deterministic reminder-test behavior on an <see cref="InProcessTestClusterBuilder"/>.
/// </summary>
public static class ReminderTestClusterBuilderExtensions
{
    /// <summary>
    /// Adds a deterministic reminder clock to the provided <see cref="InProcessTestClusterBuilder"/>.
    /// </summary>
    /// <param name="builder">The test cluster builder.</param>
    /// <param name="minimumReminderPeriod">An optional minimum reminder period override.</param>
    /// <param name="refreshReminderListPeriod">An optional reminder list refresh period override.</param>
    /// <param name="initializationTimeout">An optional reminder initialization timeout override.</param>
    /// <returns>The attached reminder test clock.</returns>
    public static ReminderTestClock AddReminderTestClock(
        this InProcessTestClusterBuilder builder,
        TimeSpan? minimumReminderPeriod = null,
        TimeSpan? refreshReminderListPeriod = null,
        TimeSpan? initializationTimeout = null)
    {
        return ReminderTestClock.Attach(builder, minimumReminderPeriod, refreshReminderListPeriod, initializationTimeout);
    }
}
