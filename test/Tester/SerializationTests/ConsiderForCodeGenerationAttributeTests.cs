
using Orleans.Serialization;
using UnitTests.GrainInterfaces;
using Xunit;

namespace Tester.SerializationTests
{
    public class ConsiderForCodeGenerationAttributeTests
    {
        public ConsiderForCodeGenerationAttributeTests()
        {
            SerializationManager.InitializeForTesting();
        }

        [Fact, TestCategory("BVT"), TestCategory("Serialization")]
        public void GenerateSerializerForNonSerializableTypeTest()
        {
            var typeUsedInGrainInterface = new SomeTypeUsedInGrainInterface { Foo = 1};
            SerializationManager.RoundTripSerializationForTesting(typeUsedInGrainInterface);
            var typeDerivedFromTypeUsedInGrainInterface = new SomeTypeDerivedFromTypeUsedInGrainInterface { Foo = 1, Bar = 2 };
            SerializationManager.RoundTripSerializationForTesting(typeDerivedFromTypeUsedInGrainInterface);
        }
    }
}
