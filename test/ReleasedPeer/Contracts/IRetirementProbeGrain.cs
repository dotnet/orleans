using Orleans;

namespace ReleasedPeer.Contracts;

public interface IRetirementProbeGrain : IGrainWithGuidKey
{
    Task<string> Locate();
    Task Block();
    Task<InvocationResult> Execute(Guid operationId, int argument);
}

[GenerateSerializer]
public sealed record InvocationResult(
    [property: Id(0)] Guid OperationId,
    [property: Id(1)] int Value,
    [property: Id(2)] string Silo,
    [property: Id(3)] string Context,
    [property: Id(4)] int EntryCount);
