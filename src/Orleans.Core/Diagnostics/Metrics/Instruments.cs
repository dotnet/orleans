using System.Diagnostics.Metrics;

namespace Orleans.Runtime;

public static class Instruments
{
    public static readonly Meter Meter = new("Microsoft.Orleans");
}
