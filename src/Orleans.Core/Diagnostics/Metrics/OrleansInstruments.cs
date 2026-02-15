using System.Diagnostics.Metrics;

namespace Orleans.Runtime;

public class OrleansInstruments(IMeterFactory meterFactory)
{
    public Meter Meter { get; } = meterFactory.Create("Microsoft.Orleans");
}
