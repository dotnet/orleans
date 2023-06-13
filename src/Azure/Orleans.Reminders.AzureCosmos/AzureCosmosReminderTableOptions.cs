namespace Orleans.Reminders.AzureCosmos;

public class AzureCosmosReminderTableOptions : AzureCosmosOptions
{
    private const string ORLEANS_REMINDERS_CONTAINER = "OrleansReminders";

    public AzureCosmosReminderTableOptions()
    {
        ContainerName = ORLEANS_REMINDERS_CONTAINER;
    }
}
