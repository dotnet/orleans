using System.Threading.Tasks;
using Orleans;

namespace UnitTests.GrainInterfaces
{
    interface ISerializerPresenceTest : IGrainWithGuidKey
    {
        Task<bool> SerializerExistsForType(System.Type param);

        Task TakeSerializedData(object data);
    }

    interface ISimpleTestTempGrain : IGrainWithGuidKey
    {
        Task SimpleMethod(MyType mt);
    }

    public class MyType
    {
        private int A;

        public void SetA(int a)
        {
            this.A = a;
        }
    }
}
