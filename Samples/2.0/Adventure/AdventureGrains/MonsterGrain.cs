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
            return Task.CompletedTask;
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
