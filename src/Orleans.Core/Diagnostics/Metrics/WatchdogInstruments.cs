using System.Diagnostics.Metrics;

namespace Orleans.Runtime;

internal static class WatchdogInstruments
{
    internal static Counter<int> HealthChecks = Instruments.Meter.CreateCounter<int>(InstrumentNames.WATCHDOG_NUM_HEALTH_CHECKS);
    internal static Counter<int> FailedHealthChecks = Instruments.Meter.CreateCounter<int>(InstrumentNames.WATCHDOG_NUM_FAILED_HEALTH_CHECKS);
}
