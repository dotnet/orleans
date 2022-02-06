using Orleans.Concurrency;

namespace AdventureGrainInterfaces;

[Immutable]
public record class MonsterInfo(
    long Id = 0,
    string? Name = null,
    List<long>? KilledBy = null);
