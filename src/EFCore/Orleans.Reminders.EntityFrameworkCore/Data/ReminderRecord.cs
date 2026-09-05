using System;
using Orleans.EntityFrameworkCore;

namespace Orleans.Reminders.EntityFrameworkCore.Data;

public class ReminderRecord<TETag>
{
    private byte[]? _serviceIdHash;
    private byte[]? _grainIdHash;
    private byte[]? _reminderNameHash;

    internal byte[] ServiceIdHash
    {
        get => _serviceIdHash ??= EFCoreIdentifierHash.Compute(ServiceId);
        set => _serviceIdHash = value;
    }

    internal byte[] GrainIdHash
    {
        get => _grainIdHash ??= EFCoreIdentifierHash.Compute(GrainId);
        set => _grainIdHash = value;
    }

    internal byte[] ReminderNameHash
    {
        get => _reminderNameHash ??= EFCoreIdentifierHash.Compute(Name);
        set => _reminderNameHash = value;
    }

    public string ServiceId { get; set; } = default!;
    public string GrainId { get; set; } = default!;
    public string Name { get; set; } = default!;
    public DateTimeOffset StartAt { get; set; }
    public TimeSpan Period { get; set; }
    public uint GrainHash { get; set; }
    public TETag ETag { get; set; } = default!;
}
