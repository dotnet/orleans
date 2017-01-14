using System;
using Orleans.Runtime;
using Orleans.Serialization;

namespace Tester.Serialization
{
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
            return typeof(FakeSerialized).IsAssignableFrom(itemType);
        }

        public object DeepCopy(object source, ICopyContext context)
        {
            DeepCopyCalled = true;
            return null;
        }

        public void Serialize(object item, ISerializationContext context, Type expectedType)
        {
            SerializeCalled = true;
            context.StreamWriter.WriteNull();
        }

        public object Deserialize(Type expectedType, IDeserializationContext context)
        {
            DeserializeCalled = true;
            context.StreamReader.ReadToken();
            return (FakeSerialized)Activator.CreateInstance(expectedType);
        }
    }
}
