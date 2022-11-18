using Chirper.Grains.Models;

namespace Chirper.Grains;

[GenerateSerializer]
public record class ChirperAccountState
{
    /// <summary>
    /// The list of publishers who this user is following.
    /// </summary>
    [Id(0)]
    public Dictionary<string, IChirperPublisher> Subscriptions { get; init; } = new();

    /// <summary>
    /// The list of subscribers who are following this user.
    /// </summary>
    [Id(1)]
    public Dictionary<string, IChirperSubscriber> Followers { get; init; } = new();

    /// <summary>
    /// Chirp messages recently received by this user.
    /// </summary>
    [Id(2)]
    public Queue<ChirperMessage> RecentReceivedMessages { get; init; } = new();

    /// <summary>
    /// Chirp messages recently published by this user.
    /// </summary>
    [Id(3)]
    public Queue<ChirperMessage> MyPublishedMessages { get; init; } = new();
}
