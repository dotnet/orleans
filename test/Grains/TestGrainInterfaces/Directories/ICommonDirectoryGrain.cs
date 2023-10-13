namespace UnitTests.GrainInterfaces.Directories
{
    public interface ICommonDirectoryGrain : IGrainWithGuidKey
    {
        Task<int> Ping();

        Task Reset();

        Task<string> GetRuntimeInstanceId();

        Task<int> ProxyPing(ICommonDirectoryGrain grain);
    }
}
