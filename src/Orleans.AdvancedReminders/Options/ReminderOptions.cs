using System;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Runtime;

namespace Orleans.AdvancedReminders;

/// <summary>
/// Options for the reminder service.
/// </summary>
public sealed class ReminderOptions
{
    /// <summary>
    /// Gets or sets the minimum period for reminders.
    /// </summary>
    /// <remarks>
    /// High-frequency reminders are dangerous for production systems.
    /// </remarks>
    public TimeSpan MinimumReminderPeriod { get; set; } = TimeSpan.FromMinutes(ReminderOptionsDefaults.MinimumReminderPeriodMinutes);

    /// <summary>
    /// Gets or sets the maximum amount of time to attempt to initialize reminders before giving up.
    /// </summary>
    /// <value>Attempt to initialize for 5 minutes before giving up by default.</value>
    public TimeSpan InitializationTimeout { get; set; } = TimeSpan.FromMinutes(ReminderOptionsDefaults.InitializationTimeoutMinutes);

    /// <summary>
    /// Gets or sets the grace period after a scheduled fire time before a reminder is considered missed.
    /// </summary>
    public TimeSpan MissedReminderGracePeriod { get; set; } = TimeSpan.FromSeconds(ReminderOptionsDefaults.MissedReminderGracePeriodSeconds);

    /// <summary>
    /// Gets or sets whether a due reminder is deleted when no active silo declares its target grain type.
    /// </summary>
    /// <remarks>
    /// The default is <see langword="false"/>. Enable this only when grain types are retired deliberately,
    /// since a type can be temporarily unavailable during deployment or cluster recovery.
    /// </remarks>
    public bool DeleteReminderWhenGrainTypeIsUnavailable { get; set; } = false;

    /// <summary>
    /// Gets or sets the Durable Jobs dequeue-count limit used to remove a repeatedly failing reminder.
    /// </summary>
    /// <remarks>
    /// This is a safety limit which prevents a broken reminder from consuming delivery resources indefinitely,
    /// not an exact count of callback exceptions. A value of <see langword="null"/> preserves the default reminder
    /// behavior: callback failures are logged and the recurring series continues. A positive value retries the same
    /// occurrence through Durable Jobs and deletes the reminder when delivery fails at or beyond that dequeue count.
    /// The Durable Jobs retry policy must allow at least this many attempts.
    /// </remarks>
    public int? MaximumDeliveryAttempts { get; set; }
}

/// <summary>
/// Validator for <see cref="ReminderOptions"/>.
/// </summary>
internal sealed partial class ReminderOptionsValidator : IConfigurationValidator
{
    private readonly ILogger<ReminderOptionsValidator> logger;
    private readonly IOptions<ReminderOptions> options;

    /// <summary>
    /// Initializes a new instance of the <see cref="ReminderOptionsValidator"/> class.
    /// </summary>
    /// <param name="logger">
    /// The logger.
    /// </param>
    /// <param name="reminderOptions">
    /// The reminder options.
    /// </param>
    public ReminderOptionsValidator(ILogger<ReminderOptionsValidator> logger, IOptions<ReminderOptions> reminderOptions)
    {
        this.logger = logger;
        options = reminderOptions;
    }

    /// <inheritdoc />
    public void ValidateConfiguration()
    {
        if (options.Value.MinimumReminderPeriod < TimeSpan.Zero)
        {
            throw new OrleansConfigurationException($"{nameof(ReminderOptions)}.{nameof(ReminderOptions.MinimumReminderPeriod)} must not be less than {TimeSpan.Zero}");
        }

        if (options.Value.MinimumReminderPeriod.TotalMinutes < ReminderOptionsDefaults.MinimumReminderPeriodMinutes)
        {
            LogWarnFastReminderInterval(options.Value.MinimumReminderPeriod, ReminderOptionsDefaults.MinimumReminderPeriodMinutes);
        }

        var maxTimerDelay = TimeSpan.FromMilliseconds(uint.MaxValue - 2L);
        if (options.Value.InitializationTimeout <= TimeSpan.Zero || options.Value.InitializationTimeout > maxTimerDelay)
        {
            throw new OrleansConfigurationException($"{nameof(ReminderOptions)}.{nameof(ReminderOptions.InitializationTimeout)} must be greater than zero and no greater than {maxTimerDelay}");
        }

        if (options.Value.MissedReminderGracePeriod <= TimeSpan.Zero)
        {
            throw new OrleansConfigurationException($"{nameof(ReminderOptions)}.{nameof(ReminderOptions.MissedReminderGracePeriod)} must be greater than {TimeSpan.Zero}");
        }

        if (options.Value.MaximumDeliveryAttempts is <= 0)
        {
            throw new OrleansConfigurationException($"{nameof(ReminderOptions)}.{nameof(ReminderOptions.MaximumDeliveryAttempts)} must be greater than zero when configured");
        }
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        EventId = (int)RSErrorCode.RS_FastReminderInterval,
        Message = $"{nameof(ReminderOptions)}.{nameof(ReminderOptions.MinimumReminderPeriod)} is {{MinimumReminderPeriod}} (default {{MinimumReminderPeriodMinutes}}). High-Frequency reminders are unsuitable for production use."
    )]
    private partial void LogWarnFastReminderInterval(TimeSpan minimumReminderPeriod, uint minimumReminderPeriodMinutes);
}
