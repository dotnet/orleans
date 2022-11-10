namespace AdventureGrainInterfaces;

/// <summary>
/// A room is any location in a game, including outdoor locations and
/// spaces that are arguably better described as moist, cold, caverns.
/// </summary>
public interface IRoomGrain : IGrainWithIntegerKey
{
    // Rooms have a textual description
    Task<string> Description(PlayerInfo whoisAsking);
    Task SetInfo(RoomInfo info);

    Task<IRoomGrain?> ExitTo(string direction);

    // Players can enter or exit a room
    Task Enter(PlayerInfo player);
    Task Exit(PlayerInfo player);

    // Monsters can enter or exit a room
    Task Enter(MonsterInfo monster);
    Task Exit(MonsterInfo monster);

    // Things can be dropped or taken from a room
    Task Drop(Thing thing);
    Task Take(Thing thing);
    Task<Thing?> FindThing(string name);

    // Players and monsters can be killed, if you have the right weapon.
    Task<PlayerInfo?> FindPlayer(string name);
    Task<MonsterInfo?> FindMonster(string name);
}
