namespace Chirper.Grains.Models;

/// <summary>
/// Data object representing one Chirp message entry
/// </summary>
[Serializable]
public record class ChirperMessage(
    /// <summary>
    /// The message content for this chirp message entry.
    /// </summary>
    string Message,

    /// <summary>
    /// The timestamp of when this chirp message entry was originally republished.
    /// </summary>
    DateTimeOffset Timestamp,

    /// <summary>
    /// The user name of the publisher of this chirp message.
    /// </summary>
    string PublisherUserName)
{
    /// <summary>
    /// The unique id of this chirp message.
    /// </summary>
    public Guid MessageId { get; } = Guid.NewGuid();

    /// <summary>
    /// Returns a string representation of this message.
    /// </summary>
    public override string ToString() =>
        $"Chirp: '{Message}' from @{PublisherUserName} at {Timestamp}";
}
