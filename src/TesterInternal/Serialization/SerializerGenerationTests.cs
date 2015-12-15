using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans.Serialization;
using Orleans.UnitTest.GrainInterfaces;

// ReSharper disable NotAccessedVariable

namespace UnitTests.Serialization
{
    /// <summary>
    /// Test the built-in serializers
    /// </summary>
    [TestClass]
    public class SerializerGenerationTests
    {
        [TestInitialize]
        public void InitializeForTesting()
        {
            SerializationManager.InitializeForTesting();
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Serialization")]
        public void SerializationTests_TypeWithInternalNestedClass()
        {
            var v = new MyTypeWithAnInternalTypeField();

            Assert.IsNotNull(SerializationManager.GetSerializer(typeof (MyTypeWithAnInternalTypeField)));
            Assert.IsNotNull(SerializationManager.GetSerializer(typeof(MyTypeWithAnInternalTypeField.MyInternalDependency)));
        }
    }
}
