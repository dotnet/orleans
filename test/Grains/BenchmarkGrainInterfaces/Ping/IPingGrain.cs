using System;
using System.Threading.Tasks;
using Orleans;
using Orleans.Concurrency;

namespace BenchmarkGrainInterfaces.Ping
{
    public interface IPingGrain : IGrainWithIntegerKey
    {
        ValueTask Run();

        [AlwaysInterleave]
        ValueTask PingPongInterleave(IPingGrain other, int count);
    }







}
