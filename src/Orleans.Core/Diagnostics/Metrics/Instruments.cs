using System.Diagnostics.Metrics;

namespace Orleans.Runtime;

internal static class Instruments
{
    internal static readonly Meter Meter = new("Microsoft.Orleans");
}
