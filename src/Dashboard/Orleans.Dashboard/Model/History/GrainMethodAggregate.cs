namespace Orleans.Dashboard.Model.History;

[GenerateSerializer]
internal struct GrainMethodAggregate
{
    [Id(0)]
    public string Grain { get; set; }

    [Id(1)]
    public string Method { get; set; }

    [Id(2)]
    public long Count { get; set; }

    [Id(3)]
    public long ExceptionCount { get; set; }

    [Id(4)]
    public double ElapsedTime { get; set; }

    [Id(5)]
    public long NumberOfSamples { get; set; }
}
