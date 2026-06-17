using System.Diagnostics.Metrics;

namespace Orleans.Runtime;

internal sealed class SchedulerInstruments(OrleansInstruments instruments)
{
    private readonly Counter<int> _longRunningTurns = instruments.Meter.CreateCounter<int>(InstrumentNames.SCHEDULER_NUM_LONG_RUNNING_TURNS);

    internal void OnLongRunningTurn()
    {
        _longRunningTurns.Add(1);
    }
}
