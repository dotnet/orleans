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
            var roomGrain = GrainClient.GrainFactory.GetGrain<IRoomGrain>(data.Id);
            await roomGrain.SetInfo(data);
            return roomGrain;
        }

        private async Task MakeThing(Thing thing)
        {
            var roomGrain = GrainClient.GrainFactory.GetGrain<IRoomGrain>(thing.FoundIn);
            await roomGrain.Drop(thing);
        }

        private async Task MakeMonster(MonsterInfo data, IRoomGrain room)
        {
            var monsterGrain = GrainClient.GrainFactory.GetGrain<IMonsterGrain>(data.Id);
            await monsterGrain.SetInfo(data);
            await monsterGrain.SetRoomGrain(room);
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
