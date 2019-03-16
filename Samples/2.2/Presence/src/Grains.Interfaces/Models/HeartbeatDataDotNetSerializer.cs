using System.IO;
using System.Runtime.Serialization.Formatters.Binary;

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
        private static readonly BinaryFormatter Formatter = new BinaryFormatter();

        public static byte[] Serialize(object o)
        {
            using (var memoryStream = new MemoryStream())
            {
                Formatter.Serialize(memoryStream, o);
                memoryStream.Flush();
                return memoryStream.ToArray();
            }
        }

        public static HeartbeatData Deserialize(byte[] data)
        {
            using (var memoryStream = new MemoryStream(data))
            {
                return (HeartbeatData)Formatter.Deserialize(memoryStream);
            }
        }
    }
}
