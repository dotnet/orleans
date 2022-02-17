using Orleans.Concurrency;

namespace AdventureGrainInterfaces;

[Immutable]
public record class PlayerInfo(
    Guid Key,
    string? Name);
