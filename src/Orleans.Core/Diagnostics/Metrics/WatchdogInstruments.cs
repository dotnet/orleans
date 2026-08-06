using System.Diagnostics.Metrics;

namespace Orleans.Runtime;

internal sealed class WatchdogInstruments(OrleansInstruments instruments)
{
    private readonly Counter<int> _healthChecks = instruments.Meter.CreateCounter<int>(InstrumentNames.WATCHDOG_NUM_HEALTH_CHECKS);
    private readonly Counter<int> _failedHealthChecks = instruments.Meter.CreateCounter<int>(InstrumentNames.WATCHDOG_NUM_FAILED_HEALTH_CHECKS);

    internal void OnHealthCheck()
    {
        _healthChecks.Add(1);
    }

    internal void OnFailedHealthCheck()
    {
        _failedHealthChecks.Add(1);
    }
}
