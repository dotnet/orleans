using System;

namespace Orleans.Dashboard.Model;

[GenerateSerializer]
internal sealed class ReminderInfo
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
}
