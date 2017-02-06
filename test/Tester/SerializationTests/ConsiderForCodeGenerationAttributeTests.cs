
using Orleans.Serialization;
using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;

namespace Tester.SerializationTests
{
    [Collection(TestEnvironmentFixture.DefaultCollection)]
    public class ConsiderForCodeGenerationAttributeTests
    {
        private readonly TestEnvironmentFixture fixture;

        public ConsiderForCodeGenerationAttributeTests(TestEnvironmentFixture fixture)
        {
            this.fixture = fixture;
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
