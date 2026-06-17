using System;
using System.Diagnostics.Metrics;

#nullable disable
namespace Orleans.Runtime;

internal sealed class ReminderInstruments(OrleansInstruments instruments)
{
    private readonly Histogram<double> _tardinessSeconds = instruments.Meter.CreateHistogram<double>(InstrumentNames.REMINDERS_TARDINESS, "seconds");
    private readonly Counter<int> _ticksDelivered = instruments.Meter.CreateCounter<int>(InstrumentNames.REMINDERS_COUNTERS_TICKS_DELIVERED);
    private ObservableGauge<int> _activeReminders;

    internal bool TardinessSecondsEnabled => _tardinessSeconds.Enabled;

    internal void RegisterActiveRemindersObserve(Func<int> observeValue)
    {
        _activeReminders = instruments.Meter.CreateObservableGauge(InstrumentNames.REMINDERS_NUMBER_ACTIVE_REMINDERS, observeValue);
    }

    internal void OnTardiness(TimeSpan tardiness) => _tardinessSeconds.Record(tardiness.TotalSeconds);

    internal void OnTickDelivered() => _ticksDelivered.Add(1);
}
