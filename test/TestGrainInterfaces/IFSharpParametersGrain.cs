using Orleans;
using UnitTests.FSharpInterfaces;

namespace UnitTests.GrainInterfaces
{
    public interface IFSharpParametersGrain<T,U> : IGrainWithGuidKey, IFSharpParameters<T>
    {
    }
}
