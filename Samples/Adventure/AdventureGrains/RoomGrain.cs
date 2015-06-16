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
using Orleans;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AdventureGrains
{
    /// <summary>
    /// Orleans grain implementation class Grain1.
    /// </summary>
    public class RoomGrain : Orleans.Grain, IRoomGrain
    {
        // TODO: replace placeholder grain interface with actual grain
        // communication interface(s).

        string description;

        List<PlayerInfo> players = new List<PlayerInfo>();
        List<MonsterInfo> monsters = new List<MonsterInfo>();
        List<Thing> things = new List<Thing>();

        Dictionary<string, IRoomGrain> exits = new Dictionary<string, IRoomGrain>();

        Task IRoomGrain.Enter(PlayerInfo player)
        {
            players.RemoveAll(x => x.Key == player.Key);
            players.Add(player);
            return TaskDone.Done;
        }

        Task IRoomGrain.Exit(PlayerInfo player)
        {
            players.RemoveAll(x => x.Key == player.Key);
            return TaskDone.Done;
        }

        Task IRoomGrain.Enter(MonsterInfo monster)
        {
            monsters.RemoveAll(x => x.Id == monster.Id);
            monsters.Add(monster);
            return TaskDone.Done;
        }

        Task IRoomGrain.Exit(MonsterInfo monster)
        {
            monsters.RemoveAll(x => x.Id == monster.Id);
            return TaskDone.Done;
        }

        Task IRoomGrain.Drop(Thing thing)
        {
            things.RemoveAll(x => x.Id == thing.Id);
            things.Add(thing);
            return TaskDone.Done;
        }

        Task IRoomGrain.Take(Thing thing)
        {
            things.RemoveAll(x => x.Name == thing.Name);
            return TaskDone.Done;
        }

        Task IRoomGrain.SetInfo(RoomInfo info)
        {
            this.description = info.Description;

            foreach (var kv in info.Directions)
            {
                this.exits[kv.Key] = GrainFactory.GetGrain<IRoomGrain>(kv.Value);
            }
            return TaskDone.Done;
        }

        Task<Thing> IRoomGrain.FindThing(string name)
        {
            return Task.FromResult(things.Where(x => x.Name == name).FirstOrDefault());
        }

        Task<PlayerInfo> IRoomGrain.FindPlayer(string name)
        {
            name = name.ToLower();
            return Task.FromResult(players.Where(x => x.Name.ToLower().Contains(name)).FirstOrDefault());
        }

        Task<MonsterInfo> IRoomGrain.FindMonster(string name)
        {
            name = name.ToLower();
            return Task.FromResult(monsters.Where(x => x.Name.ToLower().Contains(name)).FirstOrDefault());
        }

        Task<string> IRoomGrain.Description(PlayerInfo whoisAsking)
        {
            StringBuilder sb = new StringBuilder();

            sb.AppendLine(this.description);

            if (things.Count > 0)
            {
                sb.AppendLine("The following things are present:");
                foreach (var thing in things)
                {
                    sb.Append("  ").AppendLine(thing.Name);
                }
            }

            var others = players.Where(pi => pi.Key != whoisAsking.Key).ToArray();

            if (others.Length > 0 || monsters.Count > 0)
            {
                sb.AppendLine("Beware! These guys are in the room with you:");
                if (others.Length > 0)
                    foreach (var player in others)
                    {
                        sb.Append("  ").AppendLine(player.Name);
                    }
                if (monsters.Count > 0)
                    foreach (var monster in monsters)
                    {
                        sb.Append("  ").AppendLine(monster.Name);
                    }
            }

            return Task.FromResult(sb.ToString());
        }

        Task<IRoomGrain> IRoomGrain.ExitTo(string direction)
        {
            return Task.FromResult((exits.ContainsKey(direction)) ? exits[direction] : null);
        }
    }
}
