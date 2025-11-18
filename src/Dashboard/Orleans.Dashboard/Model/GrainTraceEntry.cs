using System;

namespace Orleans.Dashboard.Model;

[GenerateSerializer]
internal class GrainTraceEntry
{
    [Id(0)]
    public string PeriodKey { get; set; }

    [Id(1)]
    public DateTime Period { get; set; }

    [Id(2)]
    public string SiloAddress { get; set; }

    [Id(3)]
    public string Grain { get; set; }

    [Id(4)]
    public string Method { get; set; }

    [Id(5)]
    public long Count { get; set; }

    [Id(6)]
    public long ExceptionCount { get; set; }

    [Id(7)]
    public double ElapsedTime { get; set; }
}
