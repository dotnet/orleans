using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Orleans.AdvancedReminders;

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
