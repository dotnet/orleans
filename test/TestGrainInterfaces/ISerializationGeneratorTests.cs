using System.Threading.Tasks;
using Orleans;

namespace UnitTests.GrainInterfaces
{
    public class SerializerTestClass1
    {
        public int Field1 { get; set; }
        public int Field2 { get; set; }
    }

    public class SerializerTestClass2
    {
        public int Field1 { get; set; }
        public int Field2 { get; set; }
    }

    public class SerializerTestClass3
    {
        public int Field1 { get; set; }
        public int Field2 { get; set; }
    }

    public class SerializerTestClass4
    {
        public int Field1 { get; set; }
        public int Field2 { get; set; }
    }

    public class SerializerTestClass5
    {
        public int Field1 { get; set; }
        public int Field2 { get; set; }
    }

    public class SerializerTestClass6
    {
        public int Field1 { get; set; }
        public int Field2 { get; set; }
    }

    public interface ISerializationGeneratorTaskTest : IGrainWithIntegerKey
    {
        Task<SerializerTestClass1> Method1(SerializerTestClass2 param);

        Task<SerializerTestClass3> Method2();
    }

    public interface ISerializationGeneratorPromiseTest : IGrainWithIntegerKey
    {
        Task<SerializerTestClass4> Method1(SerializerTestClass5 param);

        Task<SerializerTestClass6> Method2();
    }
}