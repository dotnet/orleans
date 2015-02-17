using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Formatters.Binary;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans;
using Orleans.Concurrency;
using Orleans.Messaging;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.Serialization;
using Orleans.CodeGeneration;
using Orleans.UnitTest.Unsigned.GrainInterfaces;
using UnitTestGrainInterfaces;
using UnitTestGrains;
using Echo.Grains;

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

        [TestMethod, TestCategory("Nightly"), TestCategory("Serialization")]
        public void TypeWithInternalNestedClass()
        {
            var v = new MyTypeWithAnInternalTypeField();

            Assert.IsNotNull(SerializationManager.GetSerializer(typeof (MyTypeWithAnInternalTypeField)));
            Assert.IsNotNull(SerializationManager.GetSerializer(typeof(MyTypeWithAnInternalTypeField.MyInternalDependency)));
        }
    }
}
