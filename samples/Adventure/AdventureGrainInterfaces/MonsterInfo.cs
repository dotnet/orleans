namespace AdventureGrainInterfaces;

[GenerateSerializer, Immutable]
public record class MonsterInfo(
    [property: Id(0)] long Id = 0,
    [property: Id(1)] string? Name = null,
    [property: Id(2)] List<long>? KilledBy = null);
