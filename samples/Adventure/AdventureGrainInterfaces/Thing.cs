using Orleans.Concurrency;

namespace AdventureGrainInterfaces;

[Immutable]
public record class Thing(
    long Id,
    string Name,
    string Category,
    long FoundIn,
    List<string> Commands);

[Immutable]
public record class MapInfo(
    string Name,
    List<RoomInfo> Rooms,
    List<CategoryInfo> Categories,
    List<Thing> Things,
    List<MonsterInfo> Monsters);

[Immutable]
public record class RoomInfo(
    long Id,
    string Name,
    string Description,
    Dictionary<string, long> Directions);

[Immutable]
public record class CategoryInfo(
    long Id,
    string Name,
    List<string> Commands);
