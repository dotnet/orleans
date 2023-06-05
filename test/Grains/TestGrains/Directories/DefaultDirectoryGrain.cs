using UnitTests.GrainInterfaces.Directories;

namespace UnitTests.Grains.Directories
{
    [GrainType(DIRECTORY)]
    public class DefaultDirectoryGrain : Grain, IDefaultDirectoryGrain
    {
        private int counter = 0;

        public const string DIRECTORY = "Default";

        public Task<int> Ping() => Task.FromResult(++counter);

        public Task Reset()
        {
            counter = 0;
            return Task.CompletedTask;
        }

        public Task<string> GetRuntimeInstanceId() => Task.FromResult(RuntimeIdentity);

        public Task<int> ProxyPing(ICommonDirectoryGrain grain) => grain.Ping();
    }
}
