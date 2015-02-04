﻿//*********************************************************//
//    Copyright (c) Microsoft. All rights reserved.
//    
//    Apache 2.0 License
//    
//    You may obtain a copy of the License at
//    http://www.apache.org/licenses/LICENSE-2.0
//    
//    Unless required by applicable law or agreed to in writing, software 
//    distributed under the License is distributed on an "AS IS" BASIS, 
//    WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or 
//    implied. See the License for the specific language governing 
//    permissions and limitations under the License.
//
//*********************************************************

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
