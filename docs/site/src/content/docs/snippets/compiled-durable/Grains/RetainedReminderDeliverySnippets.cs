using Orleans.AdvancedReminders;
using Orleans.Hosting;

namespace Documentation.Grains.AdvancedScheduling;

internal static class RetainedReminderDeliverySnippets
{
    internal static void Configure(ISiloBuilder siloBuilder)
    {
        // <configure_advanced_reminder_retained_delivery>
        siloBuilder.AddAdvancedReminders(options =>
        {
            options.DeliveryRetryPolicy = ReminderRetryPolicy.UntilSuccess;
            options.MinimumReminderPeriod = TimeSpan.FromMinutes(1);
        });
        // </configure_advanced_reminder_retained_delivery>
    }
}
