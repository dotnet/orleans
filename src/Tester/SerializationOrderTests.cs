/*
Project Orleans Cloud Service SDK ver. 1.0
 
Copyright (c) Microsoft Corporation
 
All rights reserved.
 
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and 
associated documentation files (the ""Software""), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS
OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/


namespace Tester
{
    using System;
    using System.Collections.Generic;
    using System.Reflection;

    using Microsoft.VisualStudio.TestTools.UnitTesting;

    using Orleans.CodeGeneration;
    using Orleans.Runtime;
    using Orleans.Serialization;

    [TestClass]
    public class SerializationOrderTests
    {
        [TestInitialize]
        public void Initialize()
        {
            FakeTypeToSerialize.Reset();
            FakeSerializer1.Reset();
            FakeSerializer2.Reset();
            SerializationManager.InitializeForTesting(
                new List<TypeInfo> { typeof(FakeSerializer1).GetTypeInfo(), typeof(FakeSerializer2).GetTypeInfo() });

            SerializationManager.Register(typeof(FakeTypeToSerialize), typeof(FakeTypeToSerialize));
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Serialization")]
        public void SerializationOrder_VerifyThatExternalIsHigherPriorityThanAttributeDefined()
        {
            FakeSerializer1.SupportedTypes = FakeSerializer2.SupportedTypes = new[] { typeof(FakeTypeToSerialize) };
            var serializationItem = new FakeTypeToSerialize { SomeValue = 1 };
            SerializationManager.RoundTripSerializationForTesting(serializationItem);

            Assert.IsTrue(
                FakeSerializer1.SerializeCalled,
                "IExternalSerializer.Serialize should have been called on FakeSerializer1");
            Assert.IsTrue(
                FakeSerializer1.DeserializeCalled,
                "IExternalSerializer.Deserialize should have been called on FakeSerializer1");
            Assert.IsFalse(
                FakeTypeToSerialize.SerializeWasCalled,
                "Serialize on the type should NOT have been called");
            Assert.IsFalse(
                FakeTypeToSerialize.DeserializeWasCalled,
                "Deserialize on the type should NOT have been called");
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Serialization")]
        public void SerializationOrder_VerifyThatAttributeDefinedCalledIfNoExternalSerializersSupportType()
        {
            var serializationItem = new FakeTypeToSerialize { SomeValue = 1 };
            FakeSerializer1.SupportedTypes = FakeSerializer2.SupportedTypes = null;
            SerializationManager.RoundTripSerializationForTesting(serializationItem);
            Assert.IsTrue(FakeTypeToSerialize.SerializeWasCalled, "FakeTypeToSerialize.Serialize should have been called");
            Assert.IsTrue(FakeTypeToSerialize.DeserializeWasCalled, "FakeTypeToSerialize.Deserialize should have been called");
        }


        [TestMethod, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Serialization")]
        public void SerializationOrder_VerifyExternalSerializersInvokedInOrder()
        {
            FakeSerializer1.SupportedTypes = FakeSerializer2.SupportedTypes = new[] { typeof(FakeTypeToSerialize) };
            var serializationItem = new FakeTypeToSerialize { SomeValue = 1 };
            SerializationManager.RoundTripSerializationForTesting(serializationItem);
            Assert.IsTrue(FakeSerializer1.SerializeCalled, "IExternalSerializer.Serialize should have been called on FakeSerializer1");
            Assert.IsTrue(FakeSerializer1.DeserializeCalled, "IExternalSerializer.Deserialize should have been called on FakeSerializer1");
            Assert.IsFalse(FakeSerializer2.SerializeCalled, "IExternalSerializer.Serialize should NOT have been called on FakeSerializer2");
            Assert.IsFalse(FakeSerializer2.DeserializeCalled, "IExternalSerializer.Deserialize should NOT have been called on FakeSerializer2");
            Assert.IsFalse(FakeTypeToSerialize.SerializeWasCalled, "Serialize on the type should NOT have been called");
            Assert.IsFalse(FakeTypeToSerialize.DeserializeWasCalled, "Deserialize on the type should NOT have been called");
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

            public void Initialize(TraceLogger logger)
            {
            }

            public bool IsSupportedType(Type itemType)
            {
                IsSupportedTypeCalled = true;
                return SupportedTypes == null ? false : SupportedTypes.Contains(itemType);
            }

            public object DeepCopy(object source)
            {
                DeepCopyCalled = true;
                return source;
            }

            public void Serialize(object item, BinaryTokenStreamWriter writer, Type expectedType)
            {
                SerializeCalled = true;
            }

            public object Deserialize(Type expectedType, BinaryTokenStreamReader reader)
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

            public void Initialize(TraceLogger logger)
            {
            }

            public bool IsSupportedType(Type itemType)
            {
                IsSupportedTypeCalled = true;
                return SupportedTypes == null ? false : SupportedTypes.Contains(itemType);
            }

            public object DeepCopy(object source)
            {
                DeepCopyCalled = true;
                return source;
            }

            public void Serialize(object item, BinaryTokenStreamWriter writer, Type expectedType)
            {
                SerializeCalled = true;
            }

            public object Deserialize(Type expectedType, BinaryTokenStreamReader reader)
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
            private static object Copy(object input)
            {
                CopyWasCalled = true;
                return input;
            }

            [SerializerMethod]
            private static void Serialize(object input, BinaryTokenStreamWriter stream, Type expected)
            {
                SerializeWasCalled = true;
            }

            [DeserializerMethod]
            private static object Deserialize(Type expected, BinaryTokenStreamReader stream)
            {
                DeserializeWasCalled = true;
                return null;
            }
        }
    }
}
