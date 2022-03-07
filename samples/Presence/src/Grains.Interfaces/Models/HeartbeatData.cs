using System.Text;
using Orleans.Concurrency;

namespace Presence.Grains.Models;

/// <summary>
/// Heartbeat data for a game session.
/// This class is immutable.
/// Operations on this class always return a new copy.
/// </summary>
[Serializable]
[Immutable]
public record class HeartbeatData(
    Guid GameKey,
    GameStatus Status)
{
    /// <summary>
    /// Creates an immutable copy of the current heartbeat with the given score.
    /// </summary>
    public HeartbeatData WithNewScore(string newScore) =>
        this with { Status = Status.WithNewScore(newScore) };

    public override string ToString()
    {
        var builder = new StringBuilder();
        builder.Append($"Heartbeat:Game={GameKey}");
        builder.AppendJoin(',', Status.PlayerKeys.Select((item, idx) => $"Player{idx + 1}={item}"));
        builder.Append($",Score={Status.Score}");
        return builder.ToString();
   }
}
