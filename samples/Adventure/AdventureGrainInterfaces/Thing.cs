namespace AdventureGrainInterfaces;

[GenerateSerializer, Immutable]
public record class Thing(
    [property: Id(0)] long Id,
    [property: Id(1)] string Name,
    [property: Id(2)] string Category,
    [property: Id(3)] long FoundIn,
    [property: Id(4)] List<string> Commands);

[GenerateSerializer, Immutable]
public record class MapInfo(
    [property: Id(0)] string Name,
    [property: Id(1)] List<RoomInfo> Rooms,
    [property: Id(2)] List<CategoryInfo> Categories,
    [property: Id(3)] List<Thing> Things,
    [property: Id(4)] List<MonsterInfo> Monsters);

[GenerateSerializer, Immutable]
public record class RoomInfo(
    [property: Id(0)] long Id,
    [property: Id(1)] string Name,
    [property: Id(2)] string Description,
    [property: Id(3)] Dictionary<string, long> Directions);

[GenerateSerializer, Immutable]
public record class CategoryInfo(
    [property: Id(0)] long Id,
    [property: Id(1)] string Name,
    [property: Id(2)] List<string> Commands);
