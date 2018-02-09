using System.Collections.Generic;

using Microsoft.Extensions.Options;

using Orleans.Runtime.Configuration;
using Orleans.Runtime.ReminderService;

namespace Orleans.Hosting
{
    /// <summary>
    /// Options for <see cref="AzureBasedReminderTable"/>.
    /// </summary>
    public class AzureTableReminderStorageOptions
    {
        /// <summary>
        /// Gets or sets the storage connection string.
        /// </summary>
        public string ConnectionString { get; set; }
    }

    /// <inheritdoc />
    internal class AzureTableReminderStorageOptionsFormatter : IOptionFormatter<AzureTableReminderStorageOptions>
    {
        private readonly AzureTableReminderStorageOptions options;

        public AzureTableReminderStorageOptionsFormatter(IOptions<AzureTableReminderStorageOptions> options)
        {
            this.options = options.Value;
        }

        /// <inheritdoc />
        public string Name => nameof(AzureTableReminderStorageOptions);

        /// <inheritdoc />
        public IEnumerable<string> Format()
        {
            return new[]
            {
                OptionFormattingUtilities.Format(nameof(this.options.ConnectionString), ConfigUtilities.RedactConnectionStringInfo(this.options.ConnectionString))
            };
        }
    }
}