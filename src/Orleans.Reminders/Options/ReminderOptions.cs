using System;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using Orleans.Reminders;
using Orleans.Runtime;

namespace Orleans.Hosting;

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
    /// Gets or sets the period between reminder table refreshes.
    /// </summary>
    /// <value>Refresh the reminder table every 5 minutes by default.</value>
    public TimeSpan RefreshReminderListPeriod { get; set; } = TimeSpan.FromMinutes(ReminderOptionsDefaults.RefreshReminderListPeriodMinutes);

    /// <summary>
    /// Gets or sets the maximum amount of time to attempt to initialize reminders before giving up.
    /// </summary>
    /// <value>Attempt to initialize for 5 minutes before giving up by default.</value>
    public TimeSpan InitializationTimeout { get; set; } = TimeSpan.FromMinutes(ReminderOptionsDefaults.InitializationTimeoutMinutes);

    /// <summary>
    /// Gets or sets the look-ahead window used by the adaptive reminder service.
    /// </summary>
    public TimeSpan LookAheadWindow { get; set; } = TimeSpan.FromMinutes(ReminderOptionsDefaults.LookAheadWindowMinutes);

    /// <summary>
    /// Gets or sets the base bucket size used by the adaptive reminder service.
    /// </summary>
    public uint BaseBucketSize { get; set; } = ReminderOptionsDefaults.BaseBucketSize;

    /// <summary>
    /// Gets or sets the reminder polling interval used by the adaptive reminder service.
    /// </summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(ReminderOptionsDefaults.PollIntervalSeconds);

    /// <summary>
    /// Gets or sets a value indicating whether reminder priority is enabled in the adaptive reminder service.
    /// </summary>
    public bool EnablePriority { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether the legacy local reminder service is enabled.
    /// </summary>
    public bool EnableLegacyReminderService { get; set; } = true;
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

        if (options.Value.InitializationTimeout <= TimeSpan.Zero)
        {
            throw new OrleansConfigurationException($"{nameof(ReminderOptions)}.{nameof(ReminderOptions.InitializationTimeout)} must be greater than {TimeSpan.Zero}");
        }

        if (options.Value.LookAheadWindow <= TimeSpan.Zero)
        {
            throw new OrleansConfigurationException($"{nameof(ReminderOptions)}.{nameof(ReminderOptions.LookAheadWindow)} must be greater than {TimeSpan.Zero}");
        }

        if (options.Value.PollInterval <= TimeSpan.Zero)
        {
            throw new OrleansConfigurationException($"{nameof(ReminderOptions)}.{nameof(ReminderOptions.PollInterval)} must be greater than {TimeSpan.Zero}");
        }

        if (options.Value.BaseBucketSize == 0)
        {
            throw new OrleansConfigurationException($"{nameof(ReminderOptions)}.{nameof(ReminderOptions.BaseBucketSize)} must be greater than 0");
        }
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        EventId = (int)RSErrorCode.RS_FastReminderInterval,
        Message = $"{nameof(ReminderOptions)}.{nameof(ReminderOptions.MinimumReminderPeriod)} is {{MinimumReminderPeriod}} (default {{MinimumReminderPeriodMinutes}}. High-Frequency reminders are unsuitable for production use."
    )]
    private partial void LogWarnFastReminderInterval(TimeSpan minimumReminderPeriod, uint minimumReminderPeriodMinutes);
}
