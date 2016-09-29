using System;
using System.Collections.Generic;
using System.Reflection;
using Orleans.Runtime;
using Orleans.Serialization;
using Xunit;

namespace UnitTests.Serialization
{
    public class ExternalSerializerTest
    {
        public ExternalSerializerTest()
        {
            SerializationManager.InitializeForTesting(new List<TypeInfo> { typeof(FakeSerializer).GetTypeInfo() });
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Serialization")]
        public void SerializationTests_CustomSerializerInitialized()
        {
            Assert.True(FakeSerializer.Initialized, "The fake serializer wasn't discovered");
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Serialization")]
        public void SerializationTests_CustomSerializerIsSupportedType()
        {
            var data = new FakeSerialized { SomeData = "some data" };
            SerializationManager.RoundTripSerializationForTesting(data);

            Assert.True(FakeSerializer.IsSupportedTypeCalled, "type discovery failed");
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Serialization")]
        public void SerializationTests_ThatSerializeAndDeserializeWereInvoked()
        {
            var data = new FakeSerialized { SomeData = "some data" };
            SerializationManager.RoundTripSerializationForTesting(data);
            Assert.True(FakeSerializer.SerializeCalled);
            Assert.True(FakeSerializer.DeserializeCalled);
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Serialization")]
        public void SerializationTests_ThatCopyWasInvoked()
        {
            var data = new FakeSerialized { SomeData = "some data" };
            SerializationManager.DeepCopy(data);
            Assert.True(FakeSerializer.DeepCopyCalled);
        }
    }

    public class FakeSerialized
    {
        public string SomeData;
    }

    public class FakeSerializer : IExternalSerializer
    {
        public static bool Initialized { get; set; }

        public static bool IsSupportedTypeCalled { get; set; }

        public static bool SerializeCalled { get; set; }

        public static bool DeserializeCalled { get; set; }

        public static bool DeepCopyCalled { get; set; }

        public void Initialize(Logger logger)
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
