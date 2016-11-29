using System;
using System.Threading.Tasks;

namespace UnitTests.GrainInterfaces
{
    public interface ISimplePersistentGrain : ISimpleGrain
    {
        Task SetA(int a, bool deactivate);
        Task<Guid> GetVersion();
        Task<object> GetRequestContext();
        Task SetRequestContext(int data);
    }
}
