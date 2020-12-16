using System;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Runtime;

namespace Orleans.Configuration
{
    public class ReminderOptions
    {
        /// <summary>
        /// Minimum period for reminders. High-frequency reminders are dangerous for production systems.
        /// </summary>
        public TimeSpan MinimumReminderPeriod { get; set; } = Constants.MinReminderPeriod;
    }

    internal class ReminderOptionsValidator : IConfigurationValidator
    {
        private readonly ILogger<ReminderOptionsValidator> _logger;
        private readonly ReminderOptions _options;

        public ReminderOptionsValidator(ILogger<ReminderOptionsValidator> logger, IOptions<ReminderOptions> reminderOptions)
        {
            _logger = logger;
            _options = reminderOptions.Value;
        }

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