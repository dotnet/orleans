using System.Threading.Tasks;
using Orleans;

namespace UnitTests.GrainInterfaces
{
    interface ISerializerPresenceTest : IGrainWithGuidKey
    {
        Task<bool> SerializerExistsForType(System.Type param);

        Task TakeSerializedData(object data);
    }
}
