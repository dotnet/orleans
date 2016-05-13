using System;
using Assert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;
using Xunit;

using Orleans.Runtime.Configuration;
using System.Linq;
using Orleans.Serialization;
using Orleans.Runtime;

namespace UnitTests.Serialization
{
    public class SerializationProviderTests
    {
        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Serialization")]
        public void SerializationProvider_LoadWithSingleProviderTest()
        {
            var clientConfig = ClientConfiguration.LoadFromFile("SerializationTests\\ConfigurationTests\\ClientConfigurationForSerializer.xml");
            Assert.AreEqual(1, clientConfig.SerializationProviders.Count);
            Assert.AreEqual(typeof(FakeSerializer), clientConfig.SerializationProviders.First());
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Serialization")]
        public void SerializationProvider_LoadWithNoProvidersTest()
        {
            var clientConfig = ClientConfiguration.LoadFromFile("SerializationTests\\ConfigurationTests\\ClientConfigurationForSerializer2.xml");
            Assert.AreEqual(0, clientConfig.SerializationProviders.Count);
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Serialization")]
        public void SerializationProvider_LoadWithDuplicateProviders()
        {
            var clientConfig = ClientConfiguration.LoadFromFile("SerializationTests\\ConfigurationTests\\ClientConfigurationForSerializer3.xml");
            Assert.AreEqual(1, clientConfig.SerializationProviders.Count);
            Assert.IsTrue(clientConfig.SerializationProviders.Contains(typeof(FakeSerializer)));
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Serialization")]
        public void SerializationProvider_LoadWithMultipleProvider1()
        {
            var clientConfig = ClientConfiguration.LoadFromFile("SerializationTests\\ConfigurationTests\\ClientConfigurationForSerializer5.xml");
            Assert.AreEqual(2, clientConfig.SerializationProviders.Count);
            Assert.IsTrue(clientConfig.SerializationProviders.Contains(typeof(FakeSerializer)));
            Assert.IsTrue(clientConfig.SerializationProviders.Contains(typeof(FakeSerializer2)));
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Serialization")]
        public void SerializationProvider_LoadWithClassNotImplementingInterface()
        {
            Xunit.Assert.Throws(typeof(FormatException), ()=> ClientConfiguration.LoadFromFile("SerializationTests\\ConfigurationTests\\ClientConfigurationForSerializer6.xml"));
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Serialization")]
        public void SerializationProvider_LoadWithAbstractClass()
        {
            Xunit.Assert.Throws(typeof(FormatException), () => ClientConfiguration.LoadFromFile("SerializationTests\\ConfigurationTests\\ClientConfigurationForSerializer7.xml"));
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Serialization")]
        public void SerializationProvider_LoadWithInvalidConstructorClass()
        {
            Xunit.Assert.Throws(typeof(FormatException), () => ClientConfiguration.LoadFromFile("SerializationTests\\ConfigurationTests\\ClientConfigurationForSerializer8.xml"));
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
