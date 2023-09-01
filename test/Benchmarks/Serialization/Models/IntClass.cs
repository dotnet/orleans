using MessagePack;
using ProtoBuf;

namespace Benchmarks.Models
{
    [Serializable]
    [GenerateSerializer]
    [ProtoContract]
    [MessagePackObject]
    public sealed class IntClass
    {
        public static IntClass Create()
        {
            var result = new IntClass();
            result.Initialize();
            return result;
        }

        public void Initialize() => MyProperty1 = MyProperty2 =
            MyProperty3 = MyProperty4 = MyProperty5 = MyProperty6 = MyProperty7 = MyProperty8 = MyProperty9 = 10;

        [Id(0)]
        [ProtoMember(1)]
        [Key(0)]
        public int MyProperty1 { get; set; }

        [Id(1)]
        [ProtoMember(2)]
        [Key(1)]
        public int MyProperty2 { get; set; }

        [Id(2)]
        [ProtoMember(3)]
        [Key(2)]
        public int MyProperty3 { get; set; }

        [Id(3)]
        [ProtoMember(4)]
        [Key(3)]
        public int MyProperty4 { get; set; }

        [Id(4)]
        [ProtoMember(5)]
        [Key(4)]
        public int MyProperty5 { get; set; }

        [Id(5)]
        [ProtoMember(6)]
        [Key(5)]
        public int MyProperty6 { get; set; }

        [Id(6)]
        [ProtoMember(7)]
        [Key(6)]
        public int MyProperty7 { get; set; }

        [Id(7)]
        [ProtoMember(8)]
        [Key(7)]
        public int MyProperty8 { get; set; }

        [Id(8)]
        [ProtoMember(9)]
        [Key(8)]
        public int MyProperty9 { get; set; }
    }
}