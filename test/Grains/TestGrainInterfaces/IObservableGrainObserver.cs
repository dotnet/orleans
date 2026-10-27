using Orleans.Concurrency;

namespace UnitTests.GrainInterfaces;

/// <summary>
/// An observer which returns async enumerables, used to test observer-side async-enumerable support.
/// </summary>
public interface IObservableGrainObserver : IGrainObserver
{
    ValueTask Complete();
    ValueTask Fail();
    ValueTask OnNext(string data);

    IAsyncEnumerable<string> GetValues(CancellationToken cancellationToken = default);
    IAsyncEnumerable<int> GetValuesWithError(int errorIndex, bool waitAfterYield, string errorMessage, CancellationToken cancellationToken = default);
    IAsyncEnumerable<int> SleepyEnumerable(Guid id, TimeSpan delay, CancellationToken cancellationToken = default);
    IAsyncEnumerable<string> GetBlockingEnumerable(CancellationToken cancellationToken = default);

    [AlwaysInterleave] ValueTask WaitForCall(Guid id);
    [AlwaysInterleave] ValueTask<HashSet<Guid>> GetCanceledCalls();
    [AlwaysInterleave] ValueTask<List<(string InterfaceName, string MethodName)>> GetIncomingCalls();
}
