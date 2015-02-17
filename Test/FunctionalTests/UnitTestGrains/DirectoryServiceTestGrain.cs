using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Orleans;

namespace UnitTestGrains
{
    public class DirectoryServiceTestGrain : Grain, IDirectoryServiceTestGrain
    {
        public Task<string> GetRuntimeInstanceId()
        {
            return Task.FromResult(this.RuntimeIdentity);
        }
    }
}
