using System;
using System.Collections.Immutable;
using Orleans.Concurrency;

namespace Presence.Grains.Models
{
    /// <summary>
    /// Data about the current state of the game.
    /// </summary>
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

        public static GameStatus Empty { get; } = new GameStatus(ImmutableHashSet<Guid>.Empty, string.Empty);
    }
}
