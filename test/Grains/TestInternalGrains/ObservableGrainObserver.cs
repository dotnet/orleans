using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using UnitTests.GrainInterfaces;

namespace UnitTests.Grains;

public sealed class ObservableGrainObserver : IObservableGrainObserver, IIncomingGrainCallFilter
{
    // The implemention is done with thread-saftey in mind, because observer AlwaysInterleave calls can execute concurrently.
    private readonly ConcurrentQueue<(string InterfaceName, string MethodName)> _localCalls = new();
    private readonly Channel<string> _updates = Channel.CreateUnbounded<string>();
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource> _receivedCalls = new();
    private readonly ConcurrentDictionary<Guid, byte> _canceledCalls = new();

    private readonly TaskCompletionSource _blockingMoveNextStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _blockingEnumeratorFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task BlockingMoveNextStarted => _blockingMoveNextStarted.Task;
    public Task BlockingEnumeratorFinished => _blockingEnumeratorFinished.Task;

    public IAsyncEnumerable<string> GetValues(CancellationToken cancellationToken = default) => _updates.Reader.ReadAllAsync(cancellationToken);
    public ValueTask<HashSet<Guid>> GetCanceledCalls() => new([.. _canceledCalls.Keys]);
    public ValueTask WaitForCall(Guid id) => new(GetReceivedCallTcs(id).Task);

    private TaskCompletionSource GetReceivedCallTcs(Guid id) =>
        _receivedCalls.GetOrAdd(id, static _ => new(TaskCreationOptions.RunContinuationsAsynchronously));

    public async IAsyncEnumerable<int> GetValuesWithError(
        int errorIndex, bool waitAfterYield, string errorMessage,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        for (var i = 0; i < int.MaxValue; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (i == errorIndex)
            {
                if (errorMessage == "cancel")
                {
                    throw new OperationCanceledException(errorMessage);
                }

                throw new InvalidOperationException(errorMessage);
            }

            yield return i;

            if (waitAfterYield)
            {
                // This forces an asynchronous yield, which breaks the server-side synchronous batching loop,
                // resulting in 1 element per MoveNextAsync() request.
                await Task.Yield();
            }

            // If waitAfterYield is false, we simply fall through and loop again.
            // Because there is no incomplete await, MoveNextAsync() will complete synchronously,
            // allowing the server-side batching loop to pull up to batch size requested.
        }
    }

    public ValueTask Complete()
    {
        _updates.Writer.TryComplete();
        return default;
    }

    public ValueTask Fail()
    {
        _updates.Writer.TryComplete(new Exception("I've failed you!"));
        return default;
    }

    public ValueTask OnNext(string data) => _updates.Writer.WriteAsync(data);
    public ValueTask<List<(string InterfaceName, string MethodName)>> GetIncomingCalls() =>  new([.. _localCalls]);

    public Task Invoke(IIncomingGrainCallContext context)
    {
        _localCalls.Enqueue((context.InterfaceName, context.MethodName));
        return context.Invoke();
    }

    public async IAsyncEnumerable<int> SleepyEnumerable(Guid id, TimeSpan delay, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        try
        {
            GetReceivedCallTcs(id).TrySetResult();

            await Task.Delay(delay, cancellationToken);

            yield return 1;
        }
        finally
        {
            if (cancellationToken.IsCancellationRequested)
            {
                _canceledCalls.TryAdd(id, 0);
            }
        }
    }

    public async IAsyncEnumerable<string> GetBlockingEnumerable([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        try
        {
            yield return "first";

            _blockingMoveNextStarted.TrySetResult();

            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);

            throw new UnreachableException("This enumerator is disposed while its 2nd MoveNext is pending.");
        }
        finally
        {
            _blockingEnumeratorFinished.TrySetResult();
        }
    }
}
