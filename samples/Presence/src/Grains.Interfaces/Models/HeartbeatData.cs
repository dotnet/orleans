using System;
using System.Linq;
using System.Text;
using Orleans.Concurrency;

namespace Presence.Grains.Models
{
    /// <summary>
    /// Heartbeat data for a game session.
    /// This class is immutable.
    /// Operations on this class always return a new copy.
    /// </summary>
    [Serializable]
    [Immutable]
    public class HeartbeatData
    {
        public Guid GameKey { get; }
        public GameStatus Status { get; }

        public HeartbeatData(Guid gameKey, GameStatus status)
        {
            GameKey = gameKey;
            Status = status;
        }

        public override string ToString()
        {
            var builder = new StringBuilder();
            builder.Append($"Heartbeat:Game={GameKey}");
            builder.AppendJoin(',', Status.PlayerKeys.Select((item, idx) => $"Player{idx + 1}={item}"));
            builder.Append($",Score={Status.Score}");
            return builder.ToString();
        }

        /// <summary>
        /// Creates an immutable copy of the current heartbeat with the given score.
        /// </summary>
        public HeartbeatData WithNewScore(string newScore) => new HeartbeatData(GameKey, Status.WithNewScore(newScore));
    }
}
