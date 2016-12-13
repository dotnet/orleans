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
            reader.ReadToken();
            return (FakeSerialized)Activator.CreateInstance(expectedType);
        }
    }
}
