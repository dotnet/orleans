using System.Threading;
using System.Threading.Tasks;

namespace Orleans.Reminders.Concurrency;

/// <summary>
/// The default <see cref="IReminderDeliveryThrottle"/> registered when no concurrency
/// control is configured. Admits every acquire immediately and allocates nothing on the
/// dispatch hot path. Returns a single shared <see cref="ReminderDeliveryLease.NoOpAdmitted"/>
/// instance whose <see cref="ReminderDeliveryLease.Dispose"/> is a no-op.
/// </summary>
public sealed class NoOpReminderDeliveryThrottle : IReminderDeliveryThrottle
{
    /// <summary>The shared singleton instance.</summary>
    public static NoOpReminderDeliveryThrottle Instance { get; } = new();

    private NoOpReminderDeliveryThrottle()
    {
    }

    /// <inheritdoc />
    public ValueTask<ReminderDeliveryLease> AcquireAsync(ReminderDeliveryContext context, CancellationToken cancellationToken)
        => new(ReminderDeliveryLease.NoOpAdmitted);
}
