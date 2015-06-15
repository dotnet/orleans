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
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AdventureGrains
{
    public class MonsterGrain : Orleans.Grain, IMonsterGrain
    {
        MonsterInfo monsterInfo = new MonsterInfo();
        IRoomGrain roomGrain; // Current room

        public override Task OnActivateAsync()
        {
            this.monsterInfo.Id = this.GetPrimaryKeyLong();

            RegisterTimer((_) => Move(), null, TimeSpan.FromSeconds(150), TimeSpan.FromMinutes(150));
            return base.OnActivateAsync();
        }

        Task IMonsterGrain.SetInfo(MonsterInfo info)
        {
            this.monsterInfo = info;
            return TaskDone.Done;
        }

        Task<string> IMonsterGrain.Name()
        {
            return Task.FromResult(this.monsterInfo.Name);
        }

        async Task IMonsterGrain.SetRoomGrain(IRoomGrain room)
        {
            if (this.roomGrain != null)
                await this.roomGrain.Exit(this.monsterInfo);
            this.roomGrain = room;
            await this.roomGrain.Enter(this.monsterInfo);
        }

        Task<IRoomGrain> IMonsterGrain.RoomGrain()
        {
            return Task.FromResult(roomGrain);
        }

        async Task Move()
        {
            var directions = new string [] { "north", "south", "west", "east" };

            var rand = new Random().Next(0, 4);
            IRoomGrain nextRoom = await this.roomGrain.ExitTo(directions[rand]);

            if (null == nextRoom) 
                return;

            await this.roomGrain.Exit(this.monsterInfo);
            await nextRoom.Enter(this.monsterInfo);

            this.roomGrain = nextRoom;
        }


        Task<string> IMonsterGrain.Kill(IRoomGrain room)
        {
            if (this.roomGrain != null)
            {
                if (this.roomGrain.GetPrimaryKey() != room.GetPrimaryKey())
                {
                    return Task.FromResult(monsterInfo.Name + " snuck away. You were too slow!");
                }
                return this.roomGrain.Exit(this.monsterInfo).ContinueWith(t => monsterInfo.Name + " is dead.");
            }
            return Task.FromResult(monsterInfo.Name + " is already dead. You were too slow and someone else got to him!");
        }
    }
}
