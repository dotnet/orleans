namespace AdventureGrainInterfaces;

[GenerateSerializer, Immutable]
public record class PlayerInfo(
    Guid Key,
    string? Name);
