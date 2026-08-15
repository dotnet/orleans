#nullable disable
namespace Orleans.Dashboard.Model;

[GenerateSerializer]
[Alias("Orleans.Dashboard.Model.AdvancedReminderResponse")]
internal sealed class AdvancedReminderResponse
{
    [Id(0)]
    public int? Count { get; set; }

    [Id(1)]
    public AdvancedReminderInfo[] Reminders { get; set; }

    [Id(2)]
    public bool HasMore { get; set; }
}
