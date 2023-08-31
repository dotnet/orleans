using System.Threading.Tasks;
using Orleans;

namespace UnitTests.GrainInterfaces
{
    internal interface ISerializerPresenceTest : IGrainWithGuidKey
    {
        Task<bool> SerializerExistsForType(System.Type param);

        Task TakeSerializedData(object data);
    }
}
