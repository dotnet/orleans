using Assert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;
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

            Assert.IsNotNull(SerializationManager.GetSerializer(typeof (MyTypeWithAnInternalTypeField)));
            Assert.IsNotNull(SerializationManager.GetSerializer(typeof(MyTypeWithAnInternalTypeField.MyInternalDependency)));
        }
    }
}
