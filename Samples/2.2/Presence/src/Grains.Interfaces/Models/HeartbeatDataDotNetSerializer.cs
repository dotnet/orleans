using System.IO;
using MsgPack.Serialization;

namespace Presence.Grains.Models
{
    /// <summary>
    /// This class encapsulates serialization/deserialization of HeartbeatData.
    /// It is used only to simulate the real life scenario where data comes in from devices in the binary.
    /// If an instance of HeartbeatData is passed as an argument to a grain call, no such serializer is necessary
    /// because Orleans auto-generates efficient serializers for all argument types.
    /// </summary>
    public static class HeartbeatDataDotNetSerializer
    {
        private static readonly MessagePackSerializer<HeartbeatData> Serializer = MessagePackSerializer.Get<HeartbeatData>();

        public static byte[] Serialize(HeartbeatData item)
        {
            using (var stream = new MemoryStream())
            {
                Serializer.Pack(stream, item);
                return stream.ToArray();
            }
        }

        public static HeartbeatData Deserialize(byte[] data)
        {
            using (var stream = new MemoryStream(data))
            {
                return Serializer.Unpack(stream);
            }
        }
    }
}
