using ZeroFormatter;

namespace Benchmarks.Models
{
    [ZeroFormattable]
    public class VirtualIntsClass
    {
        public static VirtualIntsClass Create()
        {
            var result = new VirtualIntsClass();
            result.Initialize();
            return result;
        }

        public void Initialize() => MyProperty1 = MyProperty2 =
            MyProperty3 = MyProperty4 = MyProperty5 = MyProperty6 = MyProperty7 = MyProperty8 = MyProperty9 = 10;

        [Index(0)]
        public virtual int MyProperty1 { get; set; }

        [Index(1)]
        public virtual int MyProperty2 { get; set; }

        [Index(2)]
        public virtual int MyProperty3 { get; set; }

        [Index(3)]
        public virtual int MyProperty4 { get; set; }

        [Index(4)]
        public virtual int MyProperty5 { get; set; }

        [Index(5)]
        public virtual int MyProperty6 { get; set; }

        [Index(6)]
        public virtual int MyProperty7 { get; set; }

        [Index(7)]
        public virtual int MyProperty8 { get; set; }

        [Index(8)]
        public virtual int MyProperty9 { get; set; }
    }
}