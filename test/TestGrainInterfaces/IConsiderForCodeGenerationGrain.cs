
using System.Threading.Tasks;
using Orleans;

namespace UnitTests.GrainInterfaces
{
    public class SomeTypeUsedInGrainInterface
    {
        public int Foo { get; set; }
    }

    public class SomeTypeDerivedFromTypeUsedInGrainInterface : SomeTypeUsedInGrainInterface
    {
        public int Bar { get; set; }
    }

    interface IConsiderForCodeGenerationGrain : IGrain
    {
        Task SomeGrainCall(SomeTypeUsedInGrainInterface someTypeUsedInGrainInterface);
    }
}
