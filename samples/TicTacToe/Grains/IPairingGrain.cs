using Orleans;
using Orleans.Concurrency;

namespace TicTacToe.Grains;

public interface IPairingGrain : IGrainWithIntegerKey
{
    Task AddGame(Guid gameId, string name);

    Task RemoveGame(Guid gameId);

    Task<PairingSummary[]> GetGames();
}

[Immutable]
[Serializable]
public class PairingSummary
{
    public Guid GameId { get; set; }
    public string? Name { get; set; }
}
