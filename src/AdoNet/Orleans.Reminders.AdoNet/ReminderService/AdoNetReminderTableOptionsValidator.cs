using Microsoft.Extensions.Options;
using Orleans.Runtime;
using Orleans.Runtime.ReminderService;

namespace Orleans.Configuration
{
    /// <summary>
    /// Validates <see cref="AdoNetReminderTableOptions"/> configuration.
    /// </summary>
    public class AdoNetReminderTableOptionsValidator : IConfigurationValidator
    {
        private readonly AdoNetReminderTableOptions options;
        
        public AdoNetReminderTableOptionsValidator(IOptions<AdoNetReminderTableOptions> options)
        {
            this.options = options.Value;
        }

        /// <inheritdoc />
        public void ValidateConfiguration()
        {
            if (string.IsNullOrWhiteSpace(this.options.Invariant))
            {
                throw new OrleansConfigurationException($"Invalid {nameof(AdoNetReminderTableOptions)} values for {nameof(AdoNetReminderTable)}. {nameof(options.Invariant)} is required.");
            }

            if (string.IsNullOrWhiteSpace(this.options.ConnectionString) == (this.options.DataSource is null))
            {
                throw new OrleansConfigurationException($"Invalid {nameof(AdoNetReminderTableOptions)} values for {nameof(AdoNetReminderTable)}. Configure exactly one of {nameof(options.ConnectionString)} or {nameof(options.DataSource)}.");
            }
        }
    }
}