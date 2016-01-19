using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans.Serialization;
using Orleans.UnitTest.GrainInterfaces;
//using Orleans.UnitTest.Unsigned.GrainInterfaces;

//using UnitTestGrains;
//using Echo.Grains;

// ReSharper disable NotAccessedVariable

namespace UnitTests.SerializerTests
{
    /// <summary>
    /// Test the built-in serializers
    /// </summary>
    [TestClass]
    public class SerializerGenerationTests
    {
        [TestInitialize]
        public void InitializeForTesting()
        {}

        [TestMethod, TestCategory("Functional"), TestCategory("Serialization")]
        public void TypeWithInternalNestedClass()
        {
            var v = new MyTypeWithAnInternalTypeField();

            Assert.IsNotNull(SerializationManager.GetSerializer(typeof (MyTypeWithAnInternalTypeField)));
            Assert.IsNotNull(SerializationManager.GetSerializer(typeof(MyTypeWithAnInternalTypeField.MyInternalDependency)));
        }
    }
}
