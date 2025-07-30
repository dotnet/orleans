using Orleans.Concurrency;

namespace UnitTests.GrainInterfaces
{
    /// <summary>
    /// A grain which returns IAsyncEnumerable
    /// </summary>
    public interface IObservableGrain : IGrainWithGuidKey
    {
        ValueTask Complete();
        ValueTask Fail();
        ValueTask Deactivate();
        ValueTask OnNext(string data);
        IAsyncEnumerable<string> GetValues(CancellationToken cancellationToken = default);
        IAsyncEnumerable<int> GetValuesWithError(int errorIndex, bool waitAfterYield, string errorMessage, CancellationToken cancellationToken = default);
        IAsyncEnumerable<int> SleepyEnumerable(Guid id, TimeSpan delay, CancellationToken cancellationToken = default);

        [AlwaysInterleave]
        ValueTask<HashSet<Guid>> GetCanceledCalls();

        [AlwaysInterleave]
        ValueTask WaitForCall(Guid id);

        [AlwaysInterleave]
        ValueTask<List<(string InterfaceName, string MethodName)>> GetIncomingCalls();
    }
}
