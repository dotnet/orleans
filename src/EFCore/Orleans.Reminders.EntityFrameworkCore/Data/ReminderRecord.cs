using System;

namespace Orleans.Reminders.EntityFrameworkCore.Data;

public class ReminderRecord
{
    public string ServiceId { get; set; } = default!;
    public string GrainId { get; set; } = default!;
    public string Name { get; set; } = default!;
    public DateTimeOffset StartAt { get; set; }
    public TimeSpan Period { get; set; }
    public uint GrainHash { get; set; }
    public byte[] ETag { get; set; } = Array.Empty<byte>();
}