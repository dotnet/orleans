using System;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Runtime;

namespace Orleans.Configuration
{
    /// <summary>
    /// Options for the reminder service.
    /// </summary>
    public class ReminderV2Options
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
    /// Validator for <see cref="ReminderV2Options"/>.
    /// </summary>
    internal class ReminderV2OptionsValidator : IConfigurationValidator
    {
        private readonly ILogger<ReminderV2OptionsValidator> _logger;
        private readonly ReminderV2Options _options;

        /// <summary>
        /// Initializes a new instance of the <see cref="ReminderV2OptionsValidator"/> class.
        /// </summary>
        /// <param name="logger">
        /// The logger.
        /// </param>
        /// <param name="ReminderV2Options">
        /// The reminder options.
        /// </param>
        public ReminderV2OptionsValidator(ILogger<ReminderV2OptionsValidator> logger, IOptions<ReminderV2Options> ReminderV2Options)
        {
            _logger = logger;
            _options = ReminderV2Options.Value;
        }

        /// <inheritdoc />
        public void ValidateConfiguration()
        {
            if (_options.MinimumReminderPeriod < TimeSpan.Zero)
            {
                throw new OrleansConfigurationException($"{nameof(ReminderV2Options)}.{nameof(ReminderV2Options.MinimumReminderPeriod)} must not be less than {TimeSpan.Zero}");
            }

            if (_options.MinimumReminderPeriod < Constants.MinReminderPeriod)
            {
                _logger.LogWarning((int)ErrorCode.RS_FastReminderInterval,
                        $"{nameof(ReminderV2Options)}.{nameof(ReminderV2Options.MinimumReminderPeriod)} is {_options.MinimumReminderPeriod:g} (default {Constants.MinReminderPeriod:g}. High-Frequency reminders are unsuitable for production use.");
            }
        }
    }
}