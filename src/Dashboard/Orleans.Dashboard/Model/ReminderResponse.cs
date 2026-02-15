namespace Orleans.Dashboard.Model;

[GenerateSerializer]
[Alias("Orleans.Dashboard.Model.ReminderResponse")]
internal sealed class ReminderResponse
{
    [Id(0)]
    public int Count { get; set; }

    [Id(1)]
    public ReminderInfo[] Reminders { get; set; }
}
