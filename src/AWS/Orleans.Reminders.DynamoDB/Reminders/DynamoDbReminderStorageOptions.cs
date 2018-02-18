using Microsoft.Extensions.Options;
using Orleans.Reminders.DynamoDB;
using System.Collections.Generic;

namespace Orleans.Configuration
{
    /// <summary>
    /// Configuration for Amazon DynamoDB reminder storage.
    /// </summary>
    public class DynamoDBReminderStorageOptions
    {
        /// <summary>
        /// Gets or sets a unique identifier for this service, which should survive deployment and redeployment.
        /// </summary>
        public string ServiceId { get; set; } = string.Empty;

        /// <summary>
        /// AccessKey string for DynamoDB Storage
        /// </summary>
        public string AccessKey { get; set; }

        /// <summary>
        /// Secret key for DynamoDB storage
        /// </summary>
        public string SecretKey { get; set; }

        /// <summary>
        /// DynamoDB Service name 
        /// </summary>
        public string Service { get; set; }

        /// <summary>
        /// Read capacity unit for DynamoDB storage
        /// </summary>
        public int ReadCapacityUnits { get; set; } = DynamoDBStorage.DefaultReadCapacityUnits;

        /// <summary>
        /// Write capacity unit for DynamoDB storage
        /// </summary>
        public int WriteCapacityUnits { get; set; } = DynamoDBStorage.DefaultWriteCapacityUnits;

        /// <summary>
        /// DynamoDB table name.
        /// Defaults to 'OrleansReminders'.
        /// </summary>
        public string TableName { get; set; } = "OrleansReminders";
    }

    /// <inheritdoc />
    internal class DynamoDBReminderStorageOptionsFormatter : IOptionFormatter<DynamoDBReminderStorageOptions>
    {
        private readonly DynamoDBReminderStorageOptions options;

        public DynamoDBReminderStorageOptionsFormatter(IOptions<DynamoDBReminderStorageOptions> options)
        {
            this.options = options.Value;
        }

        /// <inheritdoc />
        public string Name => nameof(DynamoDBReminderStorageOptions);

        /// <inheritdoc />
        public IEnumerable<string> Format()
        {
            return new[]
            {
                OptionFormattingUtilities.Format(nameof(this.options.ServiceId),this.options.ServiceId),
                OptionFormattingUtilities.Format(nameof(this.options.TableName),this.options.TableName)
            };
        }
    }
}