using TestExtensions;

namespace UnitTests.Serialization
{
    using System;
    using System.Collections.Generic;
    using System.Reflection;
    using Orleans.CodeGeneration;
    using Orleans.Runtime;
    using Orleans.Serialization;
    using Xunit;

    public class SerializationOrderTests
    {
        public SerializationOrderTests()
        {
            FakeTypeToSerialize.Reset();
            FakeSerializer1.Reset();
            FakeSerializer2.Reset();
            SerializationTestEnvironment.Initialize(new List<TypeInfo> { typeof(FakeSerializer1).GetTypeInfo(), typeof(FakeSerializer2).GetTypeInfo() }, null);

            SerializationManager.Register(typeof(FakeTypeToSerialize), typeof(FakeTypeToSerialize));
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Serialization")]
        public void SerializationOrder_VerifyThatExternalIsHigherPriorityThanAttributeDefined()
        {
            FakeSerializer1.SupportedTypes = FakeSerializer2.SupportedTypes = new[] { typeof(FakeTypeToSerialize) };
            var serializationItem = new FakeTypeToSerialize { SomeValue = 1 };
            SerializationManager.RoundTripSerializationForTesting(serializationItem);

            Assert.True(
                FakeSerializer1.SerializeCalled,
                "IExternalSerializer.Serialize should have been called on FakeSerializer1");
            Assert.True(
                FakeSerializer1.DeserializeCalled,
                "IExternalSerializer.Deserialize should have been called on FakeSerializer1");
            Assert.False(
                FakeTypeToSerialize.SerializeWasCalled,
                "Serialize on the type should NOT have been called");
            Assert.False(
                FakeTypeToSerialize.DeserializeWasCalled,
                "Deserialize on the type should NOT have been called");
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Serialization")]
        public void SerializationOrder_VerifyThatAttributeDefinedCalledIfNoExternalSerializersSupportType()
        {
            var serializationItem = new FakeTypeToSerialize { SomeValue = 1 };
            FakeSerializer1.SupportedTypes = FakeSerializer2.SupportedTypes = null;
            SerializationManager.RoundTripSerializationForTesting(serializationItem);
            Assert.True(FakeTypeToSerialize.SerializeWasCalled, "FakeTypeToSerialize.Serialize should have been called");
            Assert.True(FakeTypeToSerialize.DeserializeWasCalled, "FakeTypeToSerialize.Deserialize should have been called");
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Serialization")]
        public void SerializationOrder_VerifyExternalSerializersInvokedInOrder()
        {
            FakeSerializer1.SupportedTypes = FakeSerializer2.SupportedTypes = new[] { typeof(FakeTypeToSerialize) };
            var serializationItem = new FakeTypeToSerialize { SomeValue = 1 };
            SerializationManager.RoundTripSerializationForTesting(serializationItem);
            Assert.True(FakeSerializer1.SerializeCalled, "IExternalSerializer.Serialize should have been called on FakeSerializer1");
            Assert.True(FakeSerializer1.DeserializeCalled, "IExternalSerializer.Deserialize should have been called on FakeSerializer1");
            Assert.False(FakeSerializer2.SerializeCalled, "IExternalSerializer.Serialize should NOT have been called on FakeSerializer2");
            Assert.False(FakeSerializer2.DeserializeCalled, "IExternalSerializer.Deserialize should NOT have been called on FakeSerializer2");
            Assert.False(FakeTypeToSerialize.SerializeWasCalled, "Serialize on the type should NOT have been called");
            Assert.False(FakeTypeToSerialize.DeserializeWasCalled, "Deserialize on the type should NOT have been called");
        }

        private class FakeSerializer1 : IExternalSerializer
        {
            public static bool IsSupportedTypeCalled { get; private set; }

            public static bool DeepCopyCalled { get; private set; }

            public static bool SerializeCalled { get; private set; }

            public static bool DeserializeCalled { get; private set; }

            public static IList<Type> SupportedTypes { get; set; }

            public static void Reset()
            {
                IsSupportedTypeCalled = DeepCopyCalled = SerializeCalled = DeserializeCalled = false;
            }

            public void Initialize(Logger logger)
            {
            }

            public bool IsSupportedType(Type itemType)
            {
                IsSupportedTypeCalled = true;
                return SupportedTypes == null ? false : SupportedTypes.Contains(itemType);
            }

            public object DeepCopy(object source, ICopyContext context)
            {
                DeepCopyCalled = true;
                return source;
            }

            public void Serialize(object item, ISerializationContext context, Type expectedType)
            {
                SerializeCalled = true;
            }

            public object Deserialize(Type expectedType, IDeserializationContext context)
            {
                DeserializeCalled = true;
                return null;
            }
        }

        private class FakeSerializer2: IExternalSerializer
        {
            public static bool IsSupportedTypeCalled { get; private set; }

            public static bool DeepCopyCalled { get; private set; }

            public static bool SerializeCalled { get; private set; }

            public static bool DeserializeCalled { get; private set; }

            public static IList<Type> SupportedTypes { get; set; }

            public static void Reset()
            {
                IsSupportedTypeCalled = DeepCopyCalled = SerializeCalled = DeserializeCalled = false;
            }

            public void Initialize(Logger logger)
            {
            }

            public bool IsSupportedType(Type itemType)
            {
                IsSupportedTypeCalled = true;
                return SupportedTypes == null ? false : SupportedTypes.Contains(itemType);
            }

            public object DeepCopy(object source, ICopyContext context)
            {
                DeepCopyCalled = true;
                return source;
            }

            public void Serialize(object item, ISerializationContext context, Type expectedType)
            {
                SerializeCalled = true;
            }

            public object Deserialize(Type expectedType, IDeserializationContext context)
            {
                DeserializeCalled = true;
                return null;
            }
        }

        private class FakeTypeToSerialize
        {
            public int SomeValue { get; set; }

            public static bool CopyWasCalled { get; private set; }

            public static bool SerializeWasCalled { get; private set; }

            public static bool DeserializeWasCalled { get; private set; }

            public static void Reset()
            {
                CopyWasCalled = SerializeWasCalled = DeserializeWasCalled = false;
            }

            [CopierMethod]
            private static object Copy(object input, ICopyContext context)
            {
                CopyWasCalled = true;
                return input;
            }

            [SerializerMethod]
            private static void Serialize(object input, ISerializationContext context, Type expected)
            {
                SerializeWasCalled = true;
            }

            [DeserializerMethod]
            private static object Deserialize(Type expected, IDeserializationContext context)
            {
                DeserializeWasCalled = true;
                return null;
            }
        }
    }
}
