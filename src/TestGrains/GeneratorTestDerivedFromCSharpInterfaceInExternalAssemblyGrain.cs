using System;
using System.Threading.Tasks;
using UnitTests.GrainInterfaces;
using Orleans;

namespace UnitTests.Grains
{
    public class GeneratorTestDerivedFromCSharpInterfaceInExternalAssemblyGrain : Grain, IGeneratorTestDerivedFromCSharpInterfaceInExternalAssemblyGrain
    {
        public Task<int> Echo(int x)
        {
            return Task.FromResult(x);
        }
    }
}
