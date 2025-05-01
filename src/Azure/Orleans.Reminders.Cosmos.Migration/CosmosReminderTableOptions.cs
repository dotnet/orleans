using Orleans.Persistence.Cosmos;

namespace Orleans.Reminders.Cosmos.Migration;

/// <summary>
/// Options for Azure Cosmos DB Reminder Storage.
/// </summary>
public class CosmosReminderTableOptions : CosmosOptions
{
    private const string ORLEANS_REMINDERS_CONTAINER = "OrleansReminders";

    /// <summary>
    /// Initializes a new <see cref="CosmosReminderTableOptions"/> instance.
    /// </summary>
    public CosmosReminderTableOptions()
    {
        ContainerName = ORLEANS_REMINDERS_CONTAINER;
    }
}
