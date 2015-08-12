/*
Project Orleans Cloud Service SDK ver. 1.0
 
Copyright (c) Microsoft Corporation
 
All rights reserved.
 
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and 
associated documentation files (the ""Software""), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS
OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

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
        public static byte[] Serialize(object o)
        {
            return SerializationManager.SerializeToByteArray(o);
        }

        public static HeartbeatData Deserialize(byte [] data)
        {
            return SerializationManager.DeserializeFromByteArray<HeartbeatData>(data);
        }
    }
}
