using System;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
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
    /// Gets or sets the initial delay used when retrying reminder persistence or durable-job scheduling.
    /// Subsequent failures use exponential backoff with jitter.
    /// </summary>
    public TimeSpan SchedulingRetryInitialDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Gets or sets the maximum delay between reminder scheduling retries.
    /// </summary>
    public TimeSpan SchedulingRetryMaxDelay { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Gets or sets how long a reminder may remain overdue with a persisted durable-job handle before
    /// reconciliation treats the handle as stale and safely attempts to recreate the job.
    /// </summary>
    public TimeSpan StaleJobRecoveryDelay { get; set; } = TimeSpan.FromMinutes(15);
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

        if (options.Value.SchedulingRetryInitialDelay <= TimeSpan.Zero
            || options.Value.SchedulingRetryInitialDelay > maxTimerDelay)
        {
            throw new OrleansConfigurationException($"{nameof(ReminderOptions)}.{nameof(ReminderOptions.SchedulingRetryInitialDelay)} must be greater than zero and no greater than {maxTimerDelay}");
        }

        if (options.Value.SchedulingRetryMaxDelay < options.Value.SchedulingRetryInitialDelay
            || options.Value.SchedulingRetryMaxDelay > maxTimerDelay)
        {
            throw new OrleansConfigurationException($"{nameof(ReminderOptions)}.{nameof(ReminderOptions.SchedulingRetryMaxDelay)} must be at least {nameof(ReminderOptions.SchedulingRetryInitialDelay)} and no greater than {maxTimerDelay}");
        }

        if (options.Value.StaleJobRecoveryDelay <= TimeSpan.Zero)
        {
            throw new OrleansConfigurationException($"{nameof(ReminderOptions)}.{nameof(ReminderOptions.StaleJobRecoveryDelay)} must be greater than {TimeSpan.Zero}");
        }
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        EventId = (int)RSErrorCode.RS_FastReminderInterval,
        Message = $"{nameof(ReminderOptions)}.{nameof(ReminderOptions.MinimumReminderPeriod)} is {{MinimumReminderPeriod}} (default {{MinimumReminderPeriodMinutes}}). High-Frequency reminders are unsuitable for production use."
    )]
    private partial void LogWarnFastReminderInterval(TimeSpan minimumReminderPeriod, uint minimumReminderPeriodMinutes);
}
