namespace UnitTests.GrainInterfaces
{
    public interface IProxyGrain : IGrainWithIntegerKey
    {
        Task CreateProxy(long key);

        Task<string> GetRuntimeInstanceId();

        Task<string> GetProxyRuntimeInstanceId();
    }
}
