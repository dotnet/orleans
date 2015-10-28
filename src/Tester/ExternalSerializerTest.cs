﻿using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using Orleans.Serialization;

namespace Tester
{
    [TestClass]
    public class ExternalSerializerTest
    {
        [TestInitialize]
        public void InitializeForTesting()
        {
            SerializationManager.InitializeForTesting();
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Serialization")]
        public void TestCustomSerializerInitialized()
        {
            Assert.IsTrue(FakeSerializer.Initialized, "The fake serializer wasn't discovered");
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Serialization")]
        public void TestCustomSerializerIsSupportedType()
        {
            var data = new FakeSerialized { SomeData = "some data" };
            SerializationManager.RoundTripSerializationForTesting(data);

            Assert.IsTrue(FakeSerializer.IsSupportedTypeCalled, "type discovery failed");
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Serialization")]
        public void TestThatSerializeAndDeserializeWereInvoked()
        {
            var data = new FakeSerialized { SomeData = "some data" };
            SerializationManager.RoundTripSerializationForTesting(data);
            Assert.IsTrue(FakeSerializer.SerializeCalled);
            Assert.IsTrue(FakeSerializer.DeserializeCalled);
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Serialization")]
        public void TestThatCopyWasInvoked()
        {
            var data = new FakeSerialized { SomeData = "some data" };
            SerializationManager.DeepCopy(data);
            Assert.IsTrue(FakeSerializer.DeepCopyCalled);
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

        public void Initialize()
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
