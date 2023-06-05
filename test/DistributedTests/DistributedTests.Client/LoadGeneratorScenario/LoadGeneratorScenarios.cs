using DistributedTests.GrainInterfaces;

namespace DistributedTests.Client.LoadGeneratorScenario
{
    public interface ILoadGeneratorScenario<TState>
    {
        string Name { get; }

        TState GetStateForWorker(IClusterClient client, int workerId);

        ValueTask IssueRequest(TState state);
    }

    public class PingScenario : ILoadGeneratorScenario<IPingGrain>
    {
        public string Name => "ping";

        public IPingGrain GetStateForWorker(IClusterClient client, int workerId) => client.GetGrain<IPingGrain>(Guid.NewGuid());

        public ValueTask IssueRequest(IPingGrain state) => state.Ping();
    }
}
