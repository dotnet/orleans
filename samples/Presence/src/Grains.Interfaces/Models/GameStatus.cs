using System;
using System.Collections.Immutable;
using Orleans.Concurrency;

namespace Presence.Grains.Models
{
    /// <summary>
    /// Data about the current state of the game.
    /// This class is immutable.
    /// Operations on this class always return a new copy.
    /// </summary>
    [Serializable]
    [Immutable]
    public class GameStatus
    {
        public ImmutableHashSet<Guid> PlayerKeys { get; }

        public string Score { get; }

        public GameStatus(ImmutableHashSet<Guid> playerKeys, string score)
        {
            PlayerKeys = playerKeys;
            Score = score;
        }

        /// <summary>
        /// Creates an immutable copy of the current game status with the given score.
        /// </summary>
        public GameStatus WithNewScore(string newScore) => new GameStatus(PlayerKeys, newScore);

        public static GameStatus Empty { get; } = new GameStatus(ImmutableHashSet<Guid>.Empty, string.Empty);
    }
}
