using System.Collections.Immutable;
using Orleans.Concurrency;

namespace Presence.Grains.Models;

/// <summary>
/// Data about the current state of the game.
/// This class is immutable.
/// Operations on this class always return a new copy.
/// </summary>
[Serializable]
[Immutable]
public record class GameStatus(
    ImmutableHashSet<Guid> PlayerKeys,
    string Score)
{
    /// <summary>
    /// Creates an immutable copy of the current game status with the given score.
    /// </summary>
    public GameStatus WithNewScore(string newScore) =>
        this with { Score = newScore };

    public static GameStatus Empty { get; } =
        new GameStatus(
            ImmutableHashSet<Guid>.Empty,
            string.Empty);
}
