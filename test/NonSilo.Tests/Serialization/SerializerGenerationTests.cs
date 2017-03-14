using Orleans.Serialization;
using Orleans.UnitTest.GrainInterfaces;
using TestExtensions;
using Xunit;

// ReSharper disable NotAccessedVariable

namespace UnitTests.Serialization
{
    /// <summary>
    /// Test the built-in serializers
    /// </summary>
    [Collection(TestEnvironmentFixture.DefaultCollection)]
    public class SerializerGenerationTests
    {
        private readonly TestEnvironmentFixture fixture;

        public SerializerGenerationTests(TestEnvironmentFixture fixture)
        {
            this.fixture = fixture;
        }

        [Fact, TestCategory("Functional"), TestCategory("Serialization")]
        public void SerializationTests_TypeWithInternalNestedClass()
        {
            var v = new MyTypeWithAnInternalTypeField();

            Assert.NotNull(this.fixture.SerializationManager.GetSerializer(typeof (MyTypeWithAnInternalTypeField)));
            Assert.NotNull(this.fixture.SerializationManager.GetSerializer(typeof(MyTypeWithAnInternalTypeField.MyInternalDependency)));
        }
    }
}
