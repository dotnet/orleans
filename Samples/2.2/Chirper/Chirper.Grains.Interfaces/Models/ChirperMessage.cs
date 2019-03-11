using System;

namespace Chirper.Grains.Models
{
    /// <summary>
    /// Data object representing one Chirp message entry
    /// </summary>
    public class ChirperMessage
    {
        /// <summary>
        /// Creates a new message.
        /// </summary>
        public ChirperMessage(string message, DateTime timestamp, string publisherUserName)
        {
            this.MessageId = Guid.NewGuid();
            this.Message = message;
            this.Timestamp = timestamp;
            this.PublisherUserName = publisherUserName;
        }

        /// <summary>
        /// The unique id of this chirp message.
        /// </summary>
        public Guid MessageId { get; }

        /// <summary>
        /// The message content for this chirp message entry.
        /// </summary>
        public string Message { get; }

        /// <summary>
        /// The timestamp of when this chirp message entry was originally republished.
        /// </summary>
        public DateTime Timestamp { get; }

        /// <summary>
        /// The user name of the publisher of this chirp message.
        /// </summary>
        public string PublisherUserName { get; }

        /// <summary>
        /// Returns a string representation of this message.
        /// </summary>
        public override string ToString() => $"Chirp: '{this.Message}' from @{this.PublisherUserName} at {this.Timestamp}";
    }
}
