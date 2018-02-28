using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Formatters.Binary;
using System.Text;
using Orleans.Serialization;

namespace Orleans.Samples.Presence.GrainInterfaces
{
    /// <summary>
    /// Data about the current state of the game
    /// </summary>
    [Serializable]
    public class GameStatus
    {
        public HashSet<Guid> Players    { get; private set; }
        public string Score             { get; set; }

        public GameStatus()
        {
            Players = new HashSet<Guid>();
        }
    }

    /// <summary>
    /// Heartbeat data for a game session
    /// </summary>
    [Serializable]
    public class HeartbeatData
    {
        public Guid Game { get; set; }
        public GameStatus Status { get; private set; }
        
        public HeartbeatData()
        {
            Status = new GameStatus();
        }

        public override string ToString()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("Heartbeat:");
            sb.Append(",Game=").Append(Game);
            Guid[] playerList = Status.Players.ToArray();
            for (int i = 0; i < playerList.Length; i++)
            {
                sb.AppendFormat(",Player{0}=", i + 1).Append(playerList[i]);
            }
            sb.AppendFormat(",Score={0}", Status.Score);
            return sb.ToString();
        }
    }


    /// <summary>
    /// This class encapsulates serialization/deserialization of HeartbeatData.
    /// It is used only to simulate the real life scenario where data comes in from devices in the binary.
    /// If an instance of HeartbeatData is passed as an argument to a grain call, no such serializer is necessary
    /// because Orleans auto-generates efficient serializers for all argument types.
    /// </summary>
    public static class HeartbeatDataDotNetSerializer
    {
        private static readonly BinaryFormatter formatter = new BinaryFormatter();

        public static byte[] Serialize(object o)
        {
            byte[] bytes;
            using (var memoryStream = new MemoryStream())
            {
                formatter.Serialize(memoryStream, o);
                memoryStream.Flush();
                bytes = memoryStream.ToArray();
            }
            return bytes;
        }

        public static HeartbeatData Deserialize(byte [] data)
        {
            using (var memoryStream = new MemoryStream(data))
            {
                return (HeartbeatData) formatter.Deserialize(memoryStream);
            }
        }
    }
}
