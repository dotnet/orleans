namespace Orleans.AdvancedReminders.Cosmos;

/// <summary>
/// Options for Azure Cosmos DB Reminder Storage.
/// </summary>
public class CosmosReminderTableOptions : CosmosOptions
{
    private const string ADVANCED_REMINDERS_CONTAINER = "OrleansAdvancedReminders";

    /// <summary>
    /// Initializes a new <see cref="CosmosReminderTableOptions"/> instance.
    /// </summary>
    public CosmosReminderTableOptions()
    {
        ContainerName = ADVANCED_REMINDERS_CONTAINER;
    }
}
