using System;

namespace Orleans.AdvancedReminders.Runtime;

/// <summary>
/// The status of a durable reminder tick when it is delivered to a grain.
/// </summary>
[Serializable]
[GenerateSerializer]
[Immutable]
public readonly struct TickStatus
{
    [Id(0)]
    public DateTime FirstTickTime { get; }

    [Id(1)]
    public TimeSpan Period { get; }

    [Id(2)]
    public DateTime CurrentTickTime { get; }

    public TickStatus(DateTime firstTickTime, TimeSpan period, DateTime currentTickTime)
    {
        FirstTickTime = firstTickTime;
        Period = period;
        CurrentTickTime = currentTickTime;
    }

    public override string ToString() => $"<{FirstTickTime}, {Period}, {CurrentTickTime}>";
}
