using System;
using System.Collections.Generic;
using System.Runtime.Serialization;
using Orleans.CodeGeneration;
using Orleans.Runtime;
using Orleans.Serialization;

namespace UnitTests.Grains
{
    public enum IntEnum
    {
        Value1,
        Value2,
        Value3
    }

    public enum UShortEnum : ushort
    {
        Value1,
        Value2,
        Value3
    }

    public enum CampaignEnemyType : sbyte
    {
        None = -1,
        Brute = 0,
        Enemy1,
        Enemy2,
        Enemy3,
        Enemy4,
    }

    public class UnserializableException : Exception
    {
        public UnserializableException(string message) : base(message)
        { }

        [CopierMethod]
        static private object Copy(object input, ICopyContext context)
        {
            return input;
        }
    }

    [Serializable]
    public class Unrecognized
    {
        public int A { get; set; }
        public int B { get; set; }
    }

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

    public class FakeSerializer1 : IExternalSerializer
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

    public class FakeSerializer2 : IExternalSerializer
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

    public class FakeTypeToSerialize
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
