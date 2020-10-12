using System;
using System.Threading.Tasks;
using Orleans;
using Orleans.CodeGeneration;
using Orleans.GrainDirectory;
using UnitTests.GrainInterfaces.Directories;

namespace UnitTests.Grains.Directories
{
    [TypeCodeOverride(TYPECODE)]
    public class DefaultDirectoryGrain : Grain, IDefaultDirectoryGrain
    {
        private int counter = 0;

        public const int TYPECODE = 1001;

        public const string DIRECTORY = "Default";

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
