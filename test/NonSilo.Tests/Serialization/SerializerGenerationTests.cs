using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Orleans.ApplicationParts;
using Orleans.Serialization;
using Orleans.UnitTest.GrainInterfaces;
using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;

// ReSharper disable NotAccessedVariable

namespace UnitTests.Serialization
{
    /// <summary>
    /// Test the built-in serializers
    /// </summary>
    [Collection(TestEnvironmentFixture.DefaultCollection)]
    [TestCategory("BVT"), TestCategory("Serialization")]
    public class SerializerGenerationTests
    {
        private readonly TestEnvironmentFixture fixture;

        public SerializerGenerationTests(TestEnvironmentFixture fixture)
        {
            this.fixture = fixture;
        }

        [Fact]
        public void SerializationTests_TypeWithInternalNestedClass()
        {
            Assert.NotNull(this.fixture.SerializationManager.GetSerializer(typeof(MyTypeWithAnInternalTypeField)));
            Assert.NotNull(this.fixture.SerializationManager.GetSerializer(typeof(MyTypeWithAnInternalTypeField.MyInternalDependency)));
        }

        /// <summary>
        /// Types with an ancestor marked with KnownBaseTypeAttribute should have serializers generated.
        /// </summary>
        [Fact]
        public void SerializationTests_TypeWithWellKnownBaseClass()
        {
            Assert.NotNull(this.fixture.SerializationManager.GetSerializer(typeof(DescendantOfWellKnownBaseClass)));
            Assert.NotNull(this.fixture.SerializationManager.GetSerializer(typeof(ImplementsWellKnownInterface)));
            
            var partManager = this.fixture.Services.GetRequiredService<IApplicationPartManager>();
            var serializerFeature = new SerializerFeature();
            partManager.PopulateFeature(serializerFeature);
            Assert.Contains(serializerFeature.SerializerTypes, s => s.Target == typeof(DescendantOfWellKnownBaseClass));
            Assert.Contains(serializerFeature.SerializerTypes, s => s.Target == typeof(ImplementsWellKnownInterface));
        }
    }
}
