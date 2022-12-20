namespace Orleans.Reminders.AzureCosmos;

public class AzureCosmosReminderTableOptions : AzureCosmosOptions
{
    private const string ORLEANS_REMINDERS_CONTAINER = "OrleansReminders";

    public AzureCosmosReminderTableOptions()
    {
        Container = ORLEANS_REMINDERS_CONTAINER;
    }
}
