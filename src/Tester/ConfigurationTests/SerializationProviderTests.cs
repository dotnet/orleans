using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using Orleans.Runtime.Configuration;
using System.Linq;
using Orleans.Serialization;
using Orleans.Runtime;

namespace Tester.ConfigurationTests
{
    [TestClass]
    public class SerializationProviderTests
    {
        [TestMethod, TestCategory("BVT"), TestCategory("Functional")]
        [DeploymentItem("ConfigurationTests\\ClientConfigurationForSerializer.xml")]
        public void LoadWithSingleProviderTest()
        {
            var clientConfig = ClientConfiguration.LoadFromFile("ClientConfigurationForSerializer.xml");
            Assert.AreEqual(1, clientConfig.SerializationProviders.Count);
            Assert.AreEqual(typeof(FakeSerializer), clientConfig.SerializationProviders.First());
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Functional")]
        [DeploymentItem("ConfigurationTests\\ClientConfigurationForSerializer2.xml")]
        public void LoadWithNoProvidersTest()
        {
            var clientConfig = ClientConfiguration.LoadFromFile("ClientConfigurationForSerializer2.xml");
            Assert.AreEqual(0, clientConfig.SerializationProviders.Count);
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Functional")]
        [DeploymentItem("ConfigurationTests\\ClientConfigurationForSerializer3.xml")]
        public void LoadWithDuplicateProviders()
        {
            var clientConfig = ClientConfiguration.LoadFromFile("ClientConfigurationForSerializer3.xml");
            Assert.AreEqual(1, clientConfig.SerializationProviders.Count);
            Assert.IsTrue(clientConfig.SerializationProviders.Contains(typeof(FakeSerializer)));
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Functional")]
        [DeploymentItem("ConfigurationTests\\ClientConfigurationForSerializer5.xml")]
        public void LoadWithMultipleProvider1()
        {
            var clientConfig = ClientConfiguration.LoadFromFile("ClientConfigurationForSerializer5.xml");
            Assert.AreEqual(2, clientConfig.SerializationProviders.Count);
            Assert.IsTrue(clientConfig.SerializationProviders.Contains(typeof(FakeSerializer)));
            Assert.IsTrue(clientConfig.SerializationProviders.Contains(typeof(FakeSerializer2)));
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Functional")]
        [DeploymentItem("ConfigurationTests\\ClientConfigurationForSerializer6.xml")]
        [ExpectedException(typeof(FormatException))]
        public void LoadWithClassNotImplementingInterface()
        {
            ClientConfiguration.LoadFromFile("ClientConfigurationForSerializer6.xml");
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Functional")]
        [DeploymentItem("ConfigurationTests\\ClientConfigurationForSerializer7.xml")]
        [ExpectedException(typeof(FormatException))]
        public void LoadWithAbstractClass()
        {
            ClientConfiguration.LoadFromFile("ClientConfigurationForSerializer7.xml");
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Functional")]
        [DeploymentItem("ConfigurationTests\\ClientConfigurationForSerializer8.xml")]
        [ExpectedException(typeof(FormatException))]
        public void LoadWithInvalidConstructorClass()
        {
            ClientConfiguration.LoadFromFile("ClientConfigurationForSerializer8.xml");
        }
    }

    public class FakeSerializer2 : AbstractFakeSerializer
    {
    }

    public class BadConstructor : AbstractFakeSerializer
    {
        internal BadConstructor() : base()
        {
        }
    }


    public abstract class AbstractFakeSerializer : IExternalSerializer
    {
        public static bool Initialized { get; set; }

        public static bool IsSupportedTypeCalled { get; set; }

        public static bool SerializeCalled { get; set; }

        public static bool DeserializeCalled { get; set; }

        public static bool DeepCopyCalled { get; set; }

        public void Initialize(TraceLogger logger)
        {
            Initialized = true;
        }

        public bool IsSupportedType(Type itemType)
        {
            IsSupportedTypeCalled = true;
            return itemType == typeof(FakeSerialized);
        }

        public object DeepCopy(object source)
        {
            DeepCopyCalled = true;
            return null;
        }

        public void Serialize(object item, BinaryTokenStreamWriter writer, Type expectedType)
        {
            SerializeCalled = true;
            writer.WriteNull();
        }

        public object Deserialize(Type expectedType, BinaryTokenStreamReader reader)
        {
            DeserializeCalled = true;
            return new FakeSerialized { SomeData = "fake deserialization" };
        }
    }
}
