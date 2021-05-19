using System.Threading.Tasks;
using Orleans;

namespace UnitTests.GrainInterfaces
{
    public interface IStatelessWorkerExceptionGrain : IGrainWithIntegerKey
    {
        Task Ping();
    }
}
