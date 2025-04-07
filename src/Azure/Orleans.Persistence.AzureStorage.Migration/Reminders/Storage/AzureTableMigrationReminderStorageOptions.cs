using Orleans.Reminders.AzureStorage;

namespace Orleans.Persistence.AzureStorage.Migration.Reminders.Storage
{
    /// <summary>
    /// Options for Reminder's storage (underlying Azure Table Storage) affecting migrated data
    /// </summary>
    public class AzureTableMigrationReminderStorageOptions : AzureTableReminderStorageOptions
    {
        public ReminderMigrationMode ReminderMigrationMode { get; set; } = ReminderMigrationMode.Disabled;
    }
}
