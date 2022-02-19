using System;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Runtime;

namespace Orleans.Configuration
{
    /// <summary>
    /// Options for the reminder service.
    /// </summary>
    public class ReminderOptions
    {
        /// <summary>
        /// Gets or sets the minimum period for reminders.
        /// </summary>
        /// <remarks>
        /// High-frequency reminders are dangerous for production systems.
        /// </remarks>
        public TimeSpan MinimumReminderPeriod { get; set; } = Constants.MinReminderPeriod;
    }

    /// <summary>
    /// Validator for <see cref="ReminderOptions"/>.
    /// </summary>
    internal class ReminderOptionsValidator : IConfigurationValidator
    {
        private readonly ILogger<ReminderOptionsValidator> _logger;
        private readonly ReminderOptions _options;

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
            _logger = logger;
            _options = reminderOptions.Value;
        }

        /// <inheritdoc />
        public void ValidateConfiguration()
        {
            if (_options.MinimumReminderPeriod < TimeSpan.Zero)
            {
                throw new OrleansConfigurationException($"{nameof(ReminderOptions)}.{nameof(ReminderOptions.MinimumReminderPeriod)} must not be less than {TimeSpan.Zero}");
            }

            if (_options.MinimumReminderPeriod < Constants.MinReminderPeriod)
            {
                _logger.LogWarning((int)ErrorCode.RS_FastReminderInterval,
                        $"{nameof(ReminderOptions)}.{nameof(ReminderOptions.MinimumReminderPeriod)} is {_options.MinimumReminderPeriod:g} (default {Constants.MinReminderPeriod:g}. High-Frequency reminders are unsuitable for production use.");
            }
        }
    }
}