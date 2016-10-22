
using Orleans.Serialization;
using UnitTests.GrainInterfaces;
using Xunit;

namespace Tester.SerializationTests
{
    using System.Collections.Generic;
    using System.Reflection;

    public class ConsiderForCodeGenerationAttributeTests
    {
        public ConsiderForCodeGenerationAttributeTests()
        {
            SerializationTestEnvironment.Initialize(null, null);
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
