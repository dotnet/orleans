using System;
using System.Runtime.Serialization;
using Orleans.Runtime;

namespace Orleans.AdvancedReminders.Runtime;

/// <summary>
/// Represents the schedule type of an advanced reminder.
/// </summary>
public enum ReminderScheduleKind : byte
{
    Interval = 0,
    Cron = 1,
}

/// <summary>
/// Priority of reminder processing.
/// </summary>
public enum ReminderPriority : byte
{
    Normal = 0,
    High = 1,
}

/// <summary>
/// Action to apply when a reminder tick was missed.
/// </summary>
public enum MissedReminderAction : byte
{
    Skip = 0,
    FireImmediately = 1,
    Notify = 2,
}

/// <summary>
/// Exception related to Orleans advanced reminder functions or reminder service.
/// </summary>
[Serializable]
[GenerateSerializer]
public sealed class ReminderException : OrleansException
{
    public ReminderException(string message) : base(message)
    {
    }

    [Obsolete]
    public ReminderException(SerializationInfo info, StreamingContext context)
        : base(info, context)
    {
    }
}
