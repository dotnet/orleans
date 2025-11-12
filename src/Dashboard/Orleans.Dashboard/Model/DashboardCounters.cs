using System;
using System.Collections.Immutable;
using System.Linq;

namespace Orleans.Dashboard.Model;

[GenerateSerializer]
internal class DashboardCounters
{
    [Id(0)]
    public SiloDetails[] Hosts { get; set; } = Array.Empty<SiloDetails>();

    [Id(1)]
    public SimpleGrainStatisticCounter[] SimpleGrainStats { get; set; } = Array.Empty<SimpleGrainStatisticCounter>();

    [Id(2)]
    public int TotalActiveHostCount { get; set; }

    [Id(3)]
    public ImmutableQueue<int> TotalActiveHostCountHistory { get; set; }

    [Id(4)]
    public int TotalActivationCount { get; set; }

    [Id(5)]
    public ImmutableQueue<int> TotalActivationCountHistory { get; set; } = ImmutableQueue<int>.Empty;

    public DashboardCounters()
    {
    }

    public DashboardCounters(int initialLength)
    {
        var values = Enumerable.Repeat(1, initialLength).Select(x => 0);

        TotalActivationCountHistory = ImmutableQueue.CreateRange(values);
        TotalActiveHostCountHistory = ImmutableQueue.CreateRange(values);
    }
}
