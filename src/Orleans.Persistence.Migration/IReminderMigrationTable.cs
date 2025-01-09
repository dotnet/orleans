namespace Orleans.Persistence.Migration
{
    public interface IReminderMigrationTable : IReminderTable
    {
        IReminderTable SourceReminderTable { get; }
        IReminderTable DestinationReminderTable { get; }
    }
}
