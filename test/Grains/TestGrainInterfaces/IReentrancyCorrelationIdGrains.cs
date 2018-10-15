using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Orleans;

namespace UnitTests.GrainInterfaces
{
    public interface IFirstGrain : IGrainWithGuidKey
    {
        Task Start(Guid guid1, Guid guid2);
    }

    public interface ISecondGrain : IGrainWithGuidKey
    {
        Task SecondGrainMethod(Guid guid);
    }

    public interface IThirdGrain : IGrainWithStringKey
    {
        Task ThirdGrainMethod(Guid userId);
    }
}
