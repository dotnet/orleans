using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Orleans;

namespace UnitTests.GrainInterfaces
{
    public interface ICallChainGrain1 : IGrainWithIntegerKey
    {
        Task<int> Run(int v);
    }

    public interface ICallChainGrain2 : IGrainWithIntegerKey
    {
        Task<int> Run(int v);
    }

    public interface ICallChainGrain3 : IGrainWithIntegerKey
    {
        Task<int> Run(int v);
    }
}
