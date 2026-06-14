using System.Threading;
using System.Threading.Tasks;

namespace Orleans.Reminders.Concurrency;

/// <summary>
/// Service provider interface (SPI) for controlling the rate and concurrency of reminder
/// tick deliveries within a silo. Implementations are resolved as a singleton from DI and
/// invoked once per reminder tick, immediately before the call to
/// <see cref="IRemindable.ReceiveReminder"/>.
/// </summary>
/// <remarks>
/// <para>The default registration is <see cref="NoOpReminderDeliveryThrottle"/>, which admits
/// every acquire immediately and allocates nothing. Calling
/// <c>AddReminderConcurrencyControl</c> on the silo builder replaces it with a configured
/// implementation.</para>
/// <para>Custom implementations may compose their own tiers (for example, Redis-backed
/// distributed limiters) and should honor the contract documented on
/// <see cref="ReminderDeliveryLease"/>: callers always dispose the returned lease exactly
/// once after the dispatch attempt, regardless of dispatch success.</para>
/// </remarks>
public interface IReminderDeliveryThrottle
{
    /// <summary>
    /// Attempts to acquire a lease admitting a single reminder tick delivery.
    /// </summary>
    /// <param name="context">Information about the reminder tick being delivered.</param>
    /// <param name="cancellationToken">
    /// Cancelled when the silo is shutting down or when the reminder's schedule has changed.
    /// A cancellation while waiting must not consume a permit and should return promptly.
    /// </param>
    /// <returns>
    /// A <see cref="ReminderDeliveryLease"/> whose <see cref="ReminderDeliveryLease.Outcome"/>
    /// indicates whether the caller may dispatch the tick.
    /// </returns>
    ValueTask<ReminderDeliveryLease> AcquireAsync(ReminderDeliveryContext context, CancellationToken cancellationToken);
}
