using System.Collections.Generic;

using Microsoft.Extensions.Options;

using Orleans.Runtime.Configuration;

namespace Orleans.Hosting
{
    /// <summary>
    /// Configuration for Amazon DynamoDB reminder storage.
    /// </summary>
    public class DynamoDBReminderTableOptions
    {
        /// <summary>
        /// Gets or sets the connection string.
        /// </summary>
        public string ConnectionString { get; set; }
    }

    /// <inheritdoc />
    internal class DynamoDBReminderTableOptionsFormatter : IOptionFormatter<DynamoDBReminderTableOptions>
    {
        private readonly DynamoDBReminderTableOptions options;

        public DynamoDBReminderTableOptionsFormatter(IOptions<DynamoDBReminderTableOptions> options)
        {
            this.options = options.Value;
        }

        /// <inheritdoc />
        public string Name => nameof(DynamoDBReminderTableOptions);

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