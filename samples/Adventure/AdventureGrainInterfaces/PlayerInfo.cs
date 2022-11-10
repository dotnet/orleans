namespace AdventureGrainInterfaces;

[GenerateSerializer, Immutable]
public record class PlayerInfo(
    [property: Id(0)] Guid Key,
    [property: Id(1)] string? Name);
