using Chirper.Grains.Models;

namespace Chirper.Grains;

[Serializable]
public record class ChirperAccountState
{
    /// <summary>
    /// The list of publishers who this user is following.
    /// </summary>
    public Dictionary<string, IChirperPublisher> Subscriptions { get; init; } = new();

    /// <summary>
    /// The list of subscribers who are following this user.
    /// </summary>
    public Dictionary<string, IChirperSubscriber> Followers { get; init; } = new();

    /// <summary>
    /// Chirp messages recently received by this user.
    /// </summary>
    public Queue<ChirperMessage> RecentReceivedMessages { get; init; } = new();

    /// <summary>
    /// Chirp messages recently published by this user.
    /// </summary>
    public Queue<ChirperMessage> MyPublishedMessages { get; init; } = new();
}
