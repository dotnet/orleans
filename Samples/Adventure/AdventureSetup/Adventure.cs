//*********************************************************//
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

using AdventureGrainInterfaces;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using Orleans;

namespace AdventureSetup
{
    public class Adventure
    {
        private async Task<IRoomGrain> MakeRoom(RoomInfo data)
        {
            var roomGrain = GrainFactory.GetGrain<IRoomGrain>(data.Id);
            await roomGrain.SetInfo(data);
            return roomGrain;
        }

        private async Task MakeThing(Thing thing)
        {
            var roomGrain = GrainFactory.GetGrain<IRoomGrain>(thing.FoundIn);
            await roomGrain.Drop(thing);
        }

        private Task MakeMonster(MonsterInfo data, IRoomGrain room)
        {
            var monsterGrain = GrainFactory.GetGrain<IMonsterGrain>(data.Id);
            monsterGrain.SetInfo(data);
            monsterGrain.SetRoomGrain(room);
            return Task.FromResult(true);
        }


        public async Task Configure(string filename)
        {
            var rand = new Random();

            var bytes = File.ReadAllText(filename);

            JavaScriptSerializer deserializer = new JavaScriptSerializer();
            var data = deserializer.Deserialize<MapInfo>(bytes);

            var rooms = new List<IRoomGrain>();
            foreach (var room in data.Rooms)
            {
                var roomGr = await MakeRoom(room);
                if (room.Id >= 0)
                    rooms.Add(roomGr);
            }
            foreach (var thing in data.Things)
            {
                await MakeThing(thing);
            }
            foreach (var monster in data.Monsters)
            {
                await MakeMonster(monster, rooms[rand.Next(0, rooms.Count)]);
            }
        }
    }
}
