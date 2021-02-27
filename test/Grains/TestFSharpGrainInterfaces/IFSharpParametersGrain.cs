using Orleans;
using UnitTests.FSharpGrains;
using UnitTests.FSharpInterfaces;

[assembly: GenerateCodeForDeclaringAssembly(typeof(Generic1ArgumentGrain<>))]

namespace UnitTests.GrainInterfaces
{
    public interface IFSharpParametersGrain<T,U> : IGrainWithGuidKey, IFSharpParameters<T>
    {
    }
}
