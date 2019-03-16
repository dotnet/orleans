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
        public ImmutableHashSet<Guid> Players { get; }
        public string Score { get; }

        public GameStatus(ImmutableHashSet<Guid> players, string score)
        {
            Players = players;
            Score = score;
        }
    }
}
