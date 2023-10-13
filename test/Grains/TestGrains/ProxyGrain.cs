using UnitTests.GrainInterfaces;

namespace TestInternalGrains
{
    public class ProxyGrain : Grain, IProxyGrain
    {
        private ITestGrain proxy;

        public Task CreateProxy(long key)
        {
            proxy = GrainFactory.GetGrain<ITestGrain>(key);
            return Task.CompletedTask;
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
