using System;
using Orleans.CodeGeneration;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.Serialization;
using TestExtensions;
using UnitTests.GrainInterfaces;
using UnitTests.Grains;
using Xunit;

namespace NonSiloTests.UnitTests.SerializerTests
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
        private static object Copy(object input, ICopyContext context)
        {
            CopyCounter++;
            var obj = input as ClassWithCustomCopier;
            return new ClassWithCustomCopier() { IntProperty = obj.IntProperty, StringProperty = obj.StringProperty };
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
        private static void Serialize(object input, ISerializationContext context, Type expected)
        {
            SerializeCounter++;
            var obj = input as ClassWithCustomSerializer;
            var stream = context.StreamWriter;
            stream.Write(obj.IntProperty);
            stream.Write(obj.StringProperty);
        }

        [DeserializerMethod]
        private static object Deserialize(Type expected, IDeserializationContext context)
        {
            DeserializeCounter++;
            var result = new ClassWithCustomSerializer();
            var stream = context.StreamReader;
            result.IntProperty = stream.ReadInt();
            result.StringProperty = stream.ReadString();
            return result;
        }
    }

    public class CustomSerializerTests
    {
        public CustomSerializerTests()
        {
            LogManager.Initialize(new NodeConfiguration());
            SerializationTestEnvironment.Initialize();
        }

        [Fact, TestCategory("Serialization")]
        public void Serialize_CustomCopier()
        {
            var original = new ClassWithCustomCopier() {IntProperty = 5, StringProperty = "Hello"};
            var copy = SerializationManager.DeepCopy(original);
            Assert.Equal(1, ClassWithCustomCopier.CopyCounter); //Custom copier was not called
        }

        [Fact, TestCategory("Serialization")]
        public void Serialize_CustomSerializer()
        {
            var original = new ClassWithCustomSerializer() { IntProperty = -3, StringProperty = "Goodbye" };
            var writeStream = new BinaryTokenStreamWriter();
            SerializationManager.Serialize(original, writeStream);
            Assert.Equal(1, ClassWithCustomSerializer.SerializeCounter); //Custom serializer was not called

            var readStream = new BinaryTokenStreamReader(writeStream.ToBytes());
            var obj = SerializationManager.Deserialize(readStream);
            Assert.Equal(1, ClassWithCustomSerializer.DeserializeCounter); //Custom deserializer was not called
        }

        [Fact, TestCategory("Serialization")]
        public void Serialize_GrainMethodTaskReturnType()
        {
            Assert.NotNull(SerializationManager.GetSerializer(typeof(SerializerTestClass1))); //No serializer generated for return type of Task grain method
        }

        [Fact, TestCategory("Serialization")]
        public void Serialize_GrainMethodTaskParamType()
        {
            Assert.NotNull(SerializationManager.GetSerializer(typeof(SerializerTestClass2))); //No serializer generated for parameter type of Task grain method
        }

        [Fact, TestCategory("Serialization")]
        public void Serialize_GrainMethodTaskReturnOnlyType()
        {
            Assert.NotNull(SerializationManager.GetSerializer(typeof(SerializerTestClass3))); //No serializer generated for return type of parameterless Task grain method
        }

        [Fact, TestCategory("Serialization")]
        public void Serialize_GrainMethodAsyncReturnType()
        {
            Assert.NotNull(SerializationManager.GetSerializer(typeof(SerializerTestClass4))); //No serializer generated for return type of Task grain method
        }

        [Fact, TestCategory("Serialization")]
        public void Serialize_GrainMethodAsyncParamType()
        {
            Assert.NotNull(SerializationManager.GetSerializer(typeof(SerializerTestClass5))); //No serializer generated for parameter type of Task grain method
        }

        [Fact, TestCategory("Serialization")]
        public void Serialize_GrainMethodAsyncReturnOnlyType()
        {
            Assert.NotNull(SerializationManager.GetSerializer(typeof(SerializerTestClass6))); //No serializer generated for return type of parameterless Task grain method
        }
        
        [Fact, TestCategory("Serialization")]
        public void Serialize_AsyncObserverArgumentType()
        {
            Assert.NotNull(SerializationManager.GetSerializer(typeof(AsyncObserverArg))); //No serializer generated for argument type of async observer

            var original = new AsyncObserverArg("A", 1);
            var obj = SerializationManager.RoundTripSerializationForTesting(original);
            Assert.Equal(original, obj); //Objects of type AsyncObserverArg aren't equal after serialization roundtrip
        }

        [Fact, TestCategory("Serialization")]
        public void Serialize_AsyncObservableArgumentType()
        {
            Assert.NotNull(SerializationManager.GetSerializer(typeof(AsyncObservableArg))); //No serializer generated for argument type of async observable
        }

        [Fact, TestCategory("Serialization")]
        public void Serialize_AsyncStreamArgumentType()
        {
            Assert.NotNull(SerializationManager.GetSerializer(typeof(AsyncStreamArg))); //No serializer generated for argument type of async stream
        }

        [Fact, TestCategory("Serialization")]
        public void Serialize_StreamSubscriptionHandleType()
        {
            Assert.NotNull(SerializationManager.GetSerializer(typeof(StreamSubscriptionHandleArg))); //No serializer generated for argument type of stream subscription handle
        }
    }
}
