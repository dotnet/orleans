namespace Orleans.Reminders.AzureCosmos;

/// <summary>
/// Options for Azure Cosmos DB Reminder Storage.
/// </summary>
public class AzureCosmosReminderTableOptions : AzureCosmosOptions
{
    private const string ORLEANS_REMINDERS_CONTAINER = "OrleansReminders";

    /// <summary>
    /// Initializes a new <see cref="AzureCosmosReminderTableOptions"/> instance.
    /// </summary>
    public AzureCosmosReminderTableOptions()
    {
        ContainerName = ORLEANS_REMINDERS_CONTAINER;
    }
}
