using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Orleans;

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
