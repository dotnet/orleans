using Orleans.Concurrency;

namespace UnitTests.GrainInterfaces;

public interface IRunRequestGrain : IGrainWithGuidKey
{
    Task StartRequestDuringCall();

    Task StartBlockingRequest();

    [AlwaysInterleave]
    Task ReleaseBlockingRequest();

    Task Ping();

    Task StartValueRequest(int value);

    Task<List<string>> QueueRequests(int count);

    Task SetRequestFilterMode(RequestFilterMode mode);

    Task StartRequestContextRequest(string value);

    Task StartFaultingRequest();

    Task StartCanceledRequest(bool cancelBeforeStart);

    [AlwaysInterleave]
    Task<string?> AwaitPendingRequest();

    [AlwaysInterleave]
    Task<List<string>> GetLog();

    [AlwaysInterleave]
    Task<List<string>> GetFilteredCalls();

    Task RetainInstance();

    Task DeactivateAndRetainInstance();
}

public interface INonReentrantRunRequestGrain : IRunRequestGrain
{
}

public interface IReentrantRunRequestGrain : IRunRequestGrain
{
}

/// <summary>
/// How the grain's call filter handles requests queued by <c>RunRequestAsync</c>.
/// </summary>
[GenerateSerializer]
public enum RequestFilterMode
{
    Invoke,
    Throw,
    ThrowAfterInvoke,
    ShortCircuit,
}
