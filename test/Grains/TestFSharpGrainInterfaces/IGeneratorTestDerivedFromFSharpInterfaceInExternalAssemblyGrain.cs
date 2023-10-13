using UnitTests.FSharpInterfaces;

namespace UnitTests.GrainInterfaces
{
    // uncomment the following interface definition to reproduce #1349

    public interface IGeneratorTestDerivedFromFSharpInterfaceInExternalAssemblyGrain : IGrainWithGuidKey, IFSharpBaseInterface
    {
    }
}
