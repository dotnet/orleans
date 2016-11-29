using System.Threading.Tasks;
using Orleans;
using UnitTests.GrainInterfaces;

namespace TestInternalGrains
{
    public class ProxyGrain : Grain, IProxyGrain
    {
        private ITestGrain proxy;

        public Task CreateProxy(long key)
        {
            proxy = GrainFactory.GetGrain<ITestGrain>(key);
            return TaskDone.Done;
        }

        public Task<string> GetRuntimeInstanceId()
        {
            return Task.FromResult(this.RuntimeIdentity);
        }

        public Task<string> GetProxyRuntimeInstanceId()
        {
            return proxy.GetRuntimeInstanceId();
        }
    }
}
