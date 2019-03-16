using System;
using System.Linq;
using System.Text;
using Orleans.Concurrency;

namespace Presence.Grains.Models
{
    /// <summary>
    /// Heartbeat data for a game session.
    /// </summary>
    [Immutable]
    public class HeartbeatData
    {
        public Guid Game { get; }
        public GameStatus Status { get; }

        public HeartbeatData(GameStatus status)
        {
            Status = status;
        }

        public override string ToString()
        {
            var builder = new StringBuilder();
            builder.Append($"Heartbeat:Game={Game}");
            builder.AppendJoin(',', Status.Players.Select((item, idx) => $"Player{idx + 1}={item}"));
            builder.Append($",Score={Status.Score}");
            return builder.ToString();
        }
    }
}
