using Orleans.Concurrency;

namespace UnitTests.GrainInterfaces;

public interface IStatelessWorkerScalingGrain : IGrainWithIntegerKey
{
    Task Wait();

    [AlwaysInterleave]
    Task Release();

    [AlwaysInterleave]
    Task<int> GetActivationCount();

    [AlwaysInterleave]
    Task<int> GetWaitingCount();
}
