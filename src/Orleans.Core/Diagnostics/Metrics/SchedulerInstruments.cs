using System.Diagnostics.Metrics;

namespace Orleans.Runtime;

internal class SchedulerInstruments
{
    internal static readonly Counter<int> LongRunningTurnsCounter = Instruments.Meter.CreateCounter<int>(InstrumentNames.SCHEDULER_NUM_LONG_RUNNING_TURNS);
}
