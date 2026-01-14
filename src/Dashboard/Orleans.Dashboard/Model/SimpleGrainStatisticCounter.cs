namespace Orleans.Dashboard.Model;

[GenerateSerializer]
internal sealed class SimpleGrainStatisticCounter
{
    [Id(0)]
    public int ActivationCount { get; set; }

    [Id(1)]
    public string GrainType { get; set; }

    [Id(2)]
    public string SiloAddress { get; set; }

    [Id(3)]
    public double TotalAwaitTime { get; set; }

    [Id(4)]
    public long TotalCalls { get; set; }

    [Id(5)]
    public double CallsPerSecond { get; set; }

    [Id(6)]
    public object TotalSeconds { get; set; }

    [Id(7)]
    public long TotalExceptions { get; set; }
}
