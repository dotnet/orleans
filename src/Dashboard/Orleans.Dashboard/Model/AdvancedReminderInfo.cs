using System;

#nullable disable
namespace Orleans.Dashboard.Model;

[GenerateSerializer]
[Alias("Orleans.Dashboard.Model.AdvancedReminderInfo")]
internal sealed class AdvancedReminderInfo
{
    [Id(0)]
    public string GrainReference { get; set; }

    [Id(1)]
    public string Name { get; set; }

    [Id(2)]
    public DateTime StartAt { get; set; }

    [Id(3)]
    public TimeSpan Period { get; set; }

    [Id(4)]
    public string PrimaryKey { get; set; }

    [Id(5)]
    public string CronExpression { get; set; }

    [Id(6)]
    public string CronTimeZoneId { get; set; }

    [Id(7)]
    public DateTime? NextDueUtc { get; set; }

    [Id(8)]
    public DateTime? LastFireUtc { get; set; }

    [Id(9)]
    public string Priority { get; set; }

    [Id(10)]
    public string MissedAction { get; set; }
}
