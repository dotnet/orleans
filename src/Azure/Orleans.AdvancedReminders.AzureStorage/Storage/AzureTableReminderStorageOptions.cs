using Azure.Storage.Blobs;

namespace Orleans.AdvancedReminders.AzureStorage
{
    /// <summary>Options for Azure Table based reminder table.</summary>
    public class AzureTableReminderStorageOptions : AzureStorageOperationOptions
    {
        /// <summary>
        /// Table name for Azure Storage
        /// </summary>
        public override string TableName { get; set; } = DEFAULT_TABLE_NAME;
        public const string DEFAULT_TABLE_NAME = "OrleansAdvancedReminders";

        /// <summary>
        /// Gets or sets the <see cref="BlobServiceClient"/> instance used to store advanced reminder jobs.
        /// </summary>
        public BlobServiceClient BlobServiceClient { get; set; } = null!;

        /// <summary>
        /// Gets or sets the container name used to store advanced reminder durable jobs.
        /// </summary>
        public string JobContainerName { get; set; } = "advanced-reminder-jobs";
    }

    /// <summary>
    /// Configuration validator for <see cref="AzureTableReminderStorageOptions"/>.
    /// </summary>
    public class AzureTableReminderStorageOptionsValidator : AzureStorageOperationOptionsValidator<AzureTableReminderStorageOptions>
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="AzureTableReminderStorageOptionsValidator"/> class.
        /// </summary>
        /// <param name="options">The option to be validated.</param>
        /// <param name="name">The option name to be validated.</param>
        public AzureTableReminderStorageOptionsValidator(AzureTableReminderStorageOptions options, string name) : base(options, name)
        {
        }
    }
}
