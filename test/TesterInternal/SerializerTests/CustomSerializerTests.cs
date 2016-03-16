using System;
using Assert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;
using Orleans.CodeGeneration;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.Serialization;
using UnitTests.GrainInterfaces;
using UnitTests.Grains;
using Xunit;

namespace UnitTests.SerializerTests
{
    [Serializable]
    public class ClassWithCustomCopier
    {
        public int IntProperty { get; set; }
        public string StringProperty { get; set; }

        public static int CopyCounter { get; set; }

        static ClassWithCustomCopier()
        {
            CopyCounter = 0;
        }

        [CopierMethod]
        private static object Copy(object input)
        {
            CopyCounter++;
            var obj = input as ClassWithCustomCopier;
            return new ClassWithCustomCopier() {IntProperty = obj.IntProperty, StringProperty = obj.StringProperty};
        }
    }

    [Serializable]
    public class ClassWithCustomSerializer
    {
        public int IntProperty { get; set; }
        public string StringProperty { get; set; }

        public static int SerializeCounter { get; set; }
        public static int DeserializeCounter { get; set; }

        static ClassWithCustomSerializer()
        {
            SerializeCounter = 0;
            DeserializeCounter = 0;
        }

        [SerializerMethod]
        private static void Serialize(object input, BinaryTokenStreamWriter stream, Type expected)
        {
            SerializeCounter++;
            var obj = input as ClassWithCustomSerializer;
            stream.Write(obj.IntProperty);
            stream.Write(obj.StringProperty);
        }

        [DeserializerMethod]
        private static object Deserialize(Type expected, BinaryTokenStreamReader stream)
        {
            DeserializeCounter++;
            var result = new ClassWithCustomSerializer();
            result.IntProperty = stream.ReadInt();
            result.StringProperty = stream.ReadString();
            return result;
        }
    }
    
    public class CustomSerializerTests
    {
        public CustomSerializerTests()
        {
            TraceLogger.Initialize(new NodeConfiguration());

            SerializationManager.InitializeForTesting();
        }

        [Fact, TestCategory("Serialization")]
        public void Serialize_CustomCopier()
        {
            var original = new ClassWithCustomCopier() {IntProperty = 5, StringProperty = "Hello"};
            var copy = SerializationManager.DeepCopy(original);
            Assert.AreEqual(1, ClassWithCustomCopier.CopyCounter, "Custom copier was not called");
        }

        [Fact, TestCategory("Serialization")]
        public void Serialize_CustomSerializer()
        {
            var original = new ClassWithCustomSerializer() { IntProperty = -3, StringProperty = "Goodbye" };
            var writeStream = new BinaryTokenStreamWriter();
            SerializationManager.Serialize(original, writeStream);
            Assert.AreEqual(1, ClassWithCustomSerializer.SerializeCounter, "Custom serializer was not called");

            var readStream = new BinaryTokenStreamReader(writeStream.ToBytes());
            var obj = SerializationManager.Deserialize(readStream);
            Assert.AreEqual(1, ClassWithCustomSerializer.DeserializeCounter, "Custom deserializer was not called");
        }

        [Fact, TestCategory("Serialization")]
        public void Serialize_GrainMethodTaskReturnType()
        {
            Assert.IsNotNull(SerializationManager.GetSerializer(typeof(SerializerTestClass1)), "No serializer generated for return type of Task grain method");
        }

        [Fact, TestCategory("Serialization")]
        public void Serialize_GrainMethodTaskParamType()
        {
            Assert.IsNotNull(SerializationManager.GetSerializer(typeof(SerializerTestClass2)), "No serializer generated for parameter type of Task grain method");
        }

        [Fact, TestCategory("Serialization")]
        public void Serialize_GrainMethodTaskReturnOnlyType()
        {
            Assert.IsNotNull(SerializationManager.GetSerializer(typeof(SerializerTestClass3)), "No serializer generated for return type of parameterless Task grain method");
        }

        [Fact, TestCategory("Serialization")]
        public void Serialize_GrainMethodAsyncReturnType()
        {
            Assert.IsNotNull(SerializationManager.GetSerializer(typeof(SerializerTestClass4)), "No serializer generated for return type of Task grain method");
        }

        [Fact, TestCategory("Serialization")]
        public void Serialize_GrainMethodAsyncParamType()
        {
            Assert.IsNotNull(SerializationManager.GetSerializer(typeof(SerializerTestClass5)), "No serializer generated for parameter type of Task grain method");
        }

        [Fact, TestCategory("Serialization")]
        public void Serialize_GrainMethodAsyncReturnOnlyType()
        {
            Assert.IsNotNull(SerializationManager.GetSerializer(typeof(SerializerTestClass6)), "No serializer generated for return type of parameterless Task grain method");
        }
        
        [Fact, TestCategory("Serialization")]
        public void Serialize_AsyncObserverArgumentType()
        {
            Assert.IsNotNull(SerializationManager.GetSerializer(typeof(AsyncObserverArg)), "No serializer generated for argument type of async observer");

            var original = new AsyncObserverArg("A", 1);
            var obj = SerializationManager.RoundTripSerializationForTesting(original);
            Assert.AreEqual(original, obj, "Objects of type AsyncObserverArg aren't equal after serialization roundtrip");
        }

        [Fact, TestCategory("Serialization")]
        public void Serialize_AsyncObservableArgumentType()
        {
            Assert.IsNotNull(SerializationManager.GetSerializer(typeof(AsyncObservableArg)), "No serializer generated for argument type of async observable");
        }

        [Fact, TestCategory("Serialization")]
        public void Serialize_AsyncStreamArgumentType()
        {
            Assert.IsNotNull(SerializationManager.GetSerializer(typeof(AsyncStreamArg)), "No serializer generated for argument type of async stream");
        }

        [Fact, TestCategory("Serialization")]
        public void Serialize_StreamSubscriptionHandleType()
        {
            Assert.IsNotNull(SerializationManager.GetSerializer(typeof(StreamSubscriptionHandleArg)), "No serializer generated for argument type of stream subscription handle");
        }
    }
}
