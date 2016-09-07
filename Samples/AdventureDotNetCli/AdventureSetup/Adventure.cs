using AdventureGrainInterfaces;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Orleans;
using Newtonsoft.Json;

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

            using (var jsonStream = new JsonTextReader(File.OpenText(filename)))
            {
                var deserializer = new JsonSerializer();
                var data = deserializer.Deserialize<MapInfo>(jsonStream);

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
}
