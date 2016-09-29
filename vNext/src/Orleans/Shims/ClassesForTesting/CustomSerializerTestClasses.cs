using System;
using Orleans.CodeGeneration;
using Orleans.Serialization;

namespace UnitTests.CustomSerializerTestClasses
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

    [Serializable]
    public class GenericArg
    {
        public string A { get; private set; }
        public int B { get; private set; }

        public GenericArg(string a, int b)
        {
            A = a;
            B = b;
        }

        public override bool Equals(object obj)
        {
            var item = obj as GenericArg;
            if (item == null)
            {
                return false;
            }

            return A.Equals(item.A) && B.Equals(item.B);
        }

        public override int GetHashCode()
        {
            return (B * 397) ^ (A != null ? A.GetHashCode() : 0);
        }
    }

    [Serializable]
    public class AsyncObserverArg : GenericArg
    {
        public AsyncObserverArg(string a, int b) : base(a, b) { }
    }

    [Serializable]
    public class AsyncObservableArg : GenericArg
    {
        public AsyncObservableArg(string a, int b) : base(a, b) { }
    }

    [Serializable]
    public class AsyncStreamArg : GenericArg
    {
        public AsyncStreamArg(string a, int b) : base(a, b) { }
    }

    [Serializable]
    public class StreamSubscriptionHandleArg : GenericArg
    {
        public StreamSubscriptionHandleArg(string a, int b) : base(a, b) { }
    }
}
