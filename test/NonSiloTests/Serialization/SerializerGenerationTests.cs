using Orleans.Serialization;
using Orleans.UnitTest.GrainInterfaces;
using Xunit;

// ReSharper disable NotAccessedVariable

namespace UnitTests.Serialization
{
    /// <summary>
    /// Test the built-in serializers
    /// </summary>
    public class SerializerGenerationTests
    {
        public SerializerGenerationTests()
        {
            SerializationManager.InitializeForTesting();
        }

        [Fact, TestCategory("Functional"), TestCategory("Serialization")]
        public void SerializationTests_TypeWithInternalNestedClass()
        {
            var v = new MyTypeWithAnInternalTypeField();

            Assert.NotNull(SerializationManager.GetSerializer(typeof (MyTypeWithAnInternalTypeField)));
            Assert.NotNull(SerializationManager.GetSerializer(typeof(MyTypeWithAnInternalTypeField.MyInternalDependency)));
        }
    }
}
