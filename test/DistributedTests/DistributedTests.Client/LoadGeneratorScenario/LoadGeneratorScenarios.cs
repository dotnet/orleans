using DistributedTests.GrainInterfaces;

namespace DistributedTests.Client.LoadGeneratorScenario
{
    public interface ILoadGeneratorScenario<TState>
    {
        string Name { get; }

        TState GetStateForWorker(IClusterClient client, int workerId);

        ValueTask IssueRequest(TState state, CancellationToken cancellationToken);
    }

    public class PingScenario : ILoadGeneratorScenario<IPingGrain>
    {
        public string Name => "ping";

        public IPingGrain GetStateForWorker(IClusterClient client, int workerId) => client.GetGrain<IPingGrain>(Guid.NewGuid());

        public ValueTask IssueRequest(IPingGrain state, CancellationToken cancellationToken) =>
            state.Ping(cancellationToken);
    }

    public class FanOutScenario : ILoadGeneratorScenario<ITreeGrain>
    {
        public string Name => "fan-out";

        public ITreeGrain GetStateForWorker(IClusterClient client, int workerId) => client.GetGrain<ITreeGrain>(primaryKey: 0, keyExtension: workerId.ToString());

        public ValueTask IssueRequest(ITreeGrain root, CancellationToken cancellationToken) =>
            root.Ping(cancellationToken);
    }
}
