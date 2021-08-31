using Orleans;
using BenchmarkGrainInterfaces.Ping;
using System.Threading.Tasks;
using Orleans.Runtime;

namespace BenchmarkGrains.Ping
{
    public class PingGrain : Grain, IPingGrain
    {
        private IPingGrain _self;

        public override Task OnActivateAsync()
        {
            _self = this.AsReference<IPingGrain>();
            return base.OnActivateAsync();
        }

        public ValueTask Run() => default;

        public ValueTask PingPongInterleave(IPingGrain other, int count)
        {
            if (count == 0) return default;
            return other.PingPongInterleave(_self, count - 1);
        }

        public ValueTask<int> GetSiloPort() => new(((ILocalSiloDetails)ServiceProvider.GetService(typeof(ILocalSiloDetails))).SiloAddress.Endpoint.Port);
    }
}
