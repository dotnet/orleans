namespace Chirper.Grains.Models;

/// <summary>
/// Data object representing one Chirp message entry
/// </summary>
/// <param name="Message">The message content for this chirp message entry.</param>
/// <param name="Timestamp">The timestamp of when this chirp message entry was originally republished.</param>
/// <param name="PublisherUserName">The user name of the publisher of this chirp message.</param>
[GenerateSerializer]
public record class ChirperMessage(
    [property: Id(0)] string Message,
    [property: Id(1)] DateTimeOffset Timestamp,
    [property: Id(2)] string PublisherUserName)
{
    /// <summary>
    /// The unique id of this chirp message.
    /// </summary>
    [Id(3)]
    public Guid MessageId { get; } = Guid.NewGuid();

    /// <summary>
    /// Returns a string representation of this message.
    /// </summary>
    public override string ToString() =>
        $"Chirp: '{Message}' from @{PublisherUserName} at {Timestamp}";
}
