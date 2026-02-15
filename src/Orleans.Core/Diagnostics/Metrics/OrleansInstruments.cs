using System.Diagnostics.Metrics;

namespace Orleans.Runtime;

public class OrleansInstruments(IMeterFactory meterFactory = null)
{
    public Meter Meter { get; } = meterFactory is null ? Instruments.Meter : meterFactory.Create("Microsoft.Orleans");
}
