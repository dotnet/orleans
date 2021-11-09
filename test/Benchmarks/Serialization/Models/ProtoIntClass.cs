namespace Benchmarks.Models
{
    public partial class ProtoIntClass
    {
        public static ProtoIntClass Create()
        {
            var result = new ProtoIntClass();
            result.Initialize();
            return result;
        }

        public void Initialize() => MyProperty1 = MyProperty2 =
            MyProperty3 = MyProperty4 = MyProperty5 = MyProperty6 = MyProperty7 = MyProperty8 = MyProperty9 = 10;
    }
}