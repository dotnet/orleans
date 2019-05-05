using System.Threading.Tasks;
using Orleans.Concurrency;

namespace Grains
{
    [StatelessWorker]
    public class LocalHealthCheckGrain : ILocalHealthCheckGrain
    {
        public Task PingAsync() => Task.CompletedTask;
    }
}
