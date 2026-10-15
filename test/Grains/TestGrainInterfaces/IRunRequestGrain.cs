using Orleans.Concurrency;

namespace UnitTests.GrainInterfaces;

public interface IRunRequestGrain : IGrainWithGuidKey
{
    Task StartRequestDuringCall();

    Task StartInterleavingRequestDuringCall();


    Task StartCustomInvokableRequest(string value);

    Task<string?> RunCancellableCustomRequest();

    Task<int> QueueUnlimitedRequests(int count);

    Task StartBlockingRequest();

    [AlwaysInterleave]
    Task ReleaseBlockingRequest();

    Task Ping();

    Task StartValueRequest(int value);

    Task<List<string>> QueueRequests(int count);

    Task<int> QueueRequestsConcurrently(int count);

    Task SetRequestFilterMode(RequestFilterMode mode);

    Task StartRequestContextRequest(string value);

    Task StartFaultingRequest(bool synchronous);

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
/// A non-reentrant grain whose <c>[MayInterleave]</c> predicate admits custom local requests.
/// </summary>
public interface IMayInterleaveRunRequestGrain : IRunRequestGrain
{
    /// <summary>
    /// Queues a custom request and returns its result once it runs, which requires interleaving with a blocking request.
    /// </summary>
    [AlwaysInterleave]
    Task<string?> RunCustomRequestDuringBlockingRequest(bool observeCompletion);
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
