using System.Threading.Channels;
using UnitTests.GrainInterfaces;

namespace UnitTests.Grains
{
    public class ObservableGrain : Grain, IObservableGrain, IIncomingGrainCallFilter
    {
        private readonly List<(string InterfaceName, string MethodName)> _localCalls = new();
        private readonly Channel<string> _updates = Channel.CreateUnbounded<string>();

        public IAsyncEnumerable<string> GetValues() => _updates.Reader.ReadAllAsync();

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
