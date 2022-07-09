namespace Orleans.Reminders.CosmosDB;

public class AzureCosmosDBReminderTableOptions : AzureCosmosDBOptions
{
    private const string ORLEANS_REMINDERS_CONTAINER = "OrleansReminders";

    public AzureCosmosDBReminderTableOptions()
    {
        this.Container = ORLEANS_REMINDERS_CONTAINER;
    }
}
