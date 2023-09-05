using Orleans.GrainDirectory;
using Orleans.Runtime;
using UnitTests.GrainInterfaces.Directories;

namespace UnitTests.Grains.Directories
{
    [GrainDirectory(GrainDirectoryName = DIRECTORY), GrainType(DIRECTORY)]
    public class CustomDirectoryGrain : ICustomDirectoryGrain
    {
        private int counter = 0;
        private readonly SiloAddress _siloAddress;

        public const string DIRECTORY = "CustomGrainDirectory";

        public CustomDirectoryGrain(ILocalSiloDetails siloDetails)
        {
            _siloAddress = siloDetails.SiloAddress;
        }

        public Task<int> Ping() => Task.FromResult(++this.counter);

        public Task Reset()
        {
            counter = 0;
            return Task.CompletedTask;
        }

        public Task<string> GetRuntimeInstanceId()
        {
            return Task.FromResult(_siloAddress.ToString());
        }

        public Task<int> ProxyPing(ICommonDirectoryGrain grain)
        {
            return grain.Ping();
        }
    }
}
