using Orleans;
using BenchmarkGrainInterfaces.Ping;
using System.Threading.Tasks;
using Orleans.Runtime;
using System.Threading;

namespace BenchmarkGrains.Ping
{
    public class PingGrain : IGrainBase, IPingGrain
    {
        private IPingGrain _self;

        public PingGrain(IGrainContext context)
        {
            GrainContext = context;
        }

        public IGrainContext GrainContext { get; set; }

        public Task OnActivateAsync(CancellationToken cancellationToken)

        {
            _self = this.AsReference<IPingGrain>();
            return Task.CompletedTask;
        }

        public ValueTask Run() => default;

        public ValueTask PingPongInterleave(IPingGrain other, int count)
        {
            if (count == 0) return default;
            return other.PingPongInterleave(_self, count - 1);
        }
    }
}
