using System.Threading.Tasks;

using Orleans;
using Orleans.Concurrency;

namespace LoadTestGrainInterfaces
{
    public interface IImplicitConsumerGrain : IGrain
    {}
}
