using AdventureGrainInterfaces;
using Orleans;

namespace AdventureGrains;

public class MonsterGrain : Grain, IMonsterGrain
{
    private MonsterInfo _monsterInfo = new();
    private IRoomGrain? _roomGrain; // Current room

    public override Task OnActivateAsync()
    {
        _monsterInfo = _monsterInfo with { Id = this.GetPrimaryKeyLong() };

        RegisterTimer(
            _ => Move(),
            null,
            TimeSpan.FromSeconds(150),
            TimeSpan.FromMinutes(150));

        return base.OnActivateAsync();
    }

    Task IMonsterGrain.SetInfo(MonsterInfo info)
    {
        _monsterInfo = info;
        return Task.CompletedTask;
    }

    Task<string?> IMonsterGrain.Name() => Task.FromResult(_monsterInfo.Name);

    async Task IMonsterGrain.SetRoomGrain(IRoomGrain room)
    {
        if (_roomGrain is not null)
        {
            await _roomGrain.Exit(_monsterInfo);
        }

        _roomGrain = room;
        await _roomGrain.Enter(_monsterInfo);
    }

    Task<IRoomGrain> IMonsterGrain.RoomGrain() => Task.FromResult(_roomGrain!);

    private async Task Move()
    {
        if (_roomGrain is not null)
        {
            var directions = new[] { "north", "south", "west", "east" };
            var rand = Random.Shared.Next(0, 4);

            var nextRoom = await _roomGrain.ExitTo(directions[rand]);
            if (nextRoom is null)
            {
                return;
            }

            await _roomGrain.Exit(_monsterInfo);
            await nextRoom.Enter(_monsterInfo);

            _roomGrain = nextRoom;
        }
    }


    Task<string> IMonsterGrain.Kill(IRoomGrain room)
    {
        if (_roomGrain is not null)
        {
            return _roomGrain.GetPrimaryKey() != room.GetPrimaryKey()
                ? Task.FromResult($"{_monsterInfo.Name} snuck away. You were too slow!")
                : _roomGrain.Exit(_monsterInfo)
                    .ContinueWith(t => $"{_monsterInfo.Name} is dead.");
        }

        return Task.FromResult(
            $"{_monsterInfo.Name} is already dead. " +
            "You were too slow and someone else got to him!");
    }
}
