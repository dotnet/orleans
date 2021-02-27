using System.Threading.Tasks;
using Orleans;
using Orleans.GrainDirectory;
using UnitTests.GrainInterfaces.Directories;

namespace UnitTests.Grains.Directories
{
    [GrainDirectory(GrainDirectoryName = DIRECTORY), GrainType(DIRECTORY)]
    public class CustomDirectoryGrain : Grain, ICustomDirectoryGrain
    {
        private int counter = 0;

        public const string DIRECTORY = "CustomGrainDirectory";

        public Task<int> Ping() => Task.FromResult(++this.counter);

        public Task Reset()
        {
            counter = 0;
            return Task.CompletedTask;
        }

        public Task<string> GetRuntimeInstanceId()
        {
            return Task.FromResult(this.RuntimeIdentity);
        }

        public Task<int> ProxyPing(ICommonDirectoryGrain grain)
        {
            return grain.Ping();
        }
    }
}
