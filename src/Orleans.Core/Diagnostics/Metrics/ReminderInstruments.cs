using System;
using System.Diagnostics.Metrics;
using System.Threading;

namespace Orleans.Runtime;

internal sealed class ReminderInstruments(OrleansInstruments instruments)
{
    private readonly Histogram<double> _tardinessSeconds = instruments.Meter.CreateHistogram<double>(InstrumentNames.REMINDERS_TARDINESS, "seconds");
    private readonly Counter<int> _ticksDelivered = instruments.Meter.CreateCounter<int>(InstrumentNames.REMINDERS_COUNTERS_TICKS_DELIVERED);
    private ObservableGauge<int> _activeReminders;
    private int _activeReminderCount;

    internal bool TardinessSecondsEnabled => _tardinessSeconds.Enabled;

    internal void RegisterActiveRemindersObserve()
    {
        _activeReminders = instruments.Meter.CreateObservableGauge(
            InstrumentNames.REMINDERS_NUMBER_ACTIVE_REMINDERS,
            () => Volatile.Read(ref _activeReminderCount),
            description: "Number of reminders which are loaded into memory and scheduled for delivery");
    }

    internal void OnLocalReminderLoaded() => Interlocked.Increment(ref _activeReminderCount);

    internal void OnLocalReminderUnloaded() => Interlocked.Decrement(ref _activeReminderCount);

    internal void OnTardiness(TimeSpan tardiness) => _tardinessSeconds.Record(tardiness.TotalSeconds);

    internal void OnTickDelivered() => _ticksDelivered.Add(1);
}
