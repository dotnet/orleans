namespace Orleans.Reminders.AzureStorage
{
    /// <summary>Options for Azure Table based reminder table.</summary>
    public class AzureTableReminderStorageOptions : AzureStorageOperationOptions
    {
        /// <summary>
        /// Table name for Azure Storage
        /// </summary>
        public override string TableName { get; set; } = DEFAULT_TABLE_NAME;
        public const string DEFAULT_TABLE_NAME = "OrleansReminders";
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
