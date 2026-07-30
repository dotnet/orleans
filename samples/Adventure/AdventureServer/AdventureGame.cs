using AdventureGrainInterfaces;
using Newtonsoft.Json;

namespace AdventureSetup;

public sealed class AdventureGame
{
    private readonly IGrainFactory _client;

    public AdventureGame(IGrainFactory client) => _client = client;

    private async Task<IRoomGrain> MakeRoom(RoomInfo data)
    {
        var roomGrain = _client.GetGrain<IRoomGrain>(data.Id);
        await roomGrain.SetInfo(data);
        return roomGrain;
    }

    private async Task MakeThing(Thing thing)
    {
        var roomGrain = _client.GetGrain<IRoomGrain>(thing.FoundIn);
        await roomGrain.Drop(thing);
    }

    private async Task MakeMonster(MonsterInfo data, IRoomGrain room)
    {
        var monsterGrain = _client.GetGrain<IMonsterGrain>(data.Id);
        await monsterGrain.SetInfo(data);
        await monsterGrain.SetRoomGrain(room);
    }

    public async Task Configure(string filename)
    {
        var rand = Random.Shared;

        // Read the contents of the game file and deserialize it
        var jsonData = await File.ReadAllTextAsync(filename);
        var data = JsonConvert.DeserializeObject<MapInfo>(jsonData)!;

        // Initialize the game world using the game data
        var rooms = new List<IRoomGrain>();
        foreach (var room in data.Rooms)
        {
            var roomGr = await MakeRoom(room);
            if (room.Id >= 0)
            {
                rooms.Add(roomGr);
            }
        }

        foreach (var thing in data.Things)
        {
            await MakeThing(thing);
        }

        foreach (var monster in data.Monsters)
        {
            await MakeMonster(
                monster,
                rooms[rand.Next(0, rooms.Count)]);
        }
    }
}
