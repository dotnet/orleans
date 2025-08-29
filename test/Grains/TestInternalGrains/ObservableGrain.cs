using System.Runtime.CompilerServices;
using System.Threading.Channels;
using UnitTests.GrainInterfaces;

namespace UnitTests.Grains
{
    public class ObservableGrain : Grain, IObservableGrain, IIncomingGrainCallFilter
    {
        private readonly List<(string InterfaceName, string MethodName)> _localCalls = new();
        private readonly Channel<string> _updates = Channel.CreateUnbounded<string>();
        private readonly Dictionary<Guid, TaskCompletionSource> _receivedCalls = [];
        private readonly HashSet<Guid> _canceledCalls = [];

        public IAsyncEnumerable<string> GetValues(CancellationToken cancellationToken) => _updates.Reader.ReadAllAsync(cancellationToken);
        public ValueTask<HashSet<Guid>> GetCanceledCalls() => new(_canceledCalls);
        public ValueTask WaitForCall(Guid id)
        {
            var tcs = GetReceivedCallTcs(id);

            return new(tcs.Task);
        }

        private TaskCompletionSource GetReceivedCallTcs(Guid id)
        {
            if (!_receivedCalls.TryGetValue(id, out var tcs))
            {
                tcs = _receivedCalls[id] = new();
            }

            return tcs;
        }

        public async IAsyncEnumerable<int> GetValuesWithError(int errorIndex, bool waitAfterYield, string errorMessage, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
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
                await Task.Yield();
            }
        }

        public ValueTask Complete()
        {
            _updates.Writer.Complete();
            return default;
        }

        public ValueTask Fail()
        {
            _updates.Writer.Complete(new Exception("I've failed you!"));
            return default;
        }

        public ValueTask Deactivate()
        {
            DeactivateOnIdle();
            return default;
        }

        public ValueTask OnNext(string data) => _updates.Writer.WriteAsync(data);

        public ValueTask<List<(string InterfaceName, string MethodName)>> GetIncomingCalls() => new(_localCalls);

        public Task Invoke(IIncomingGrainCallContext context)
        {
            _localCalls.Add((context.InterfaceName, context.MethodName));
            return context.Invoke();
        }

        public async IAsyncEnumerable<int> SleepyEnumerable(Guid id, TimeSpan delay, [EnumeratorCancellation] CancellationToken cancellationToken = default)
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
                    _canceledCalls.Add(id);
                }
            }
        }
    }
}
