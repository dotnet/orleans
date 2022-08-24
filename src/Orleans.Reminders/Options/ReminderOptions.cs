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
}

/// <summary>
/// Validator for <see cref="ReminderOptions"/>.
/// </summary>
internal sealed class ReminderOptionsValidator : IConfigurationValidator
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
            logger.LogWarning((int)RSErrorCode.RS_FastReminderInterval,
                    $"{nameof(ReminderOptions)}.{nameof(ReminderOptions.MinimumReminderPeriod)} is {options.Value.MinimumReminderPeriod:g} (default {ReminderOptionsDefaults.MinimumReminderPeriodMinutes:g}. High-Frequency reminders are unsuitable for production use.");
        }
    }
}
