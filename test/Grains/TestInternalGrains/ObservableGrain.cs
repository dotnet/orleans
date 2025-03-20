using System.Runtime.CompilerServices;
using System.Threading.Channels;
using UnitTests.GrainInterfaces;

namespace UnitTests.Grains
{
    public class ObservableGrain : Grain, IObservableGrain, IIncomingGrainCallFilter
    {
        private readonly List<(string InterfaceName, string MethodName)> _localCalls = new();
        private readonly Channel<string> _updates = Channel.CreateUnbounded<string>();

        public IAsyncEnumerable<string> GetValues(CancellationToken cancellationToken) => _updates.Reader.ReadAllAsync(cancellationToken);

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
    }
}
