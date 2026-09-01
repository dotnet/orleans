using System;

namespace Orleans.Streams
{
    /// <summary>
    /// Specifies how a rewindable persistent stream subscription selects its initial position.
    /// </summary>
    public readonly struct StreamSubscriptionOptions
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="StreamSubscriptionOptions"/> struct.
        /// </summary>
        /// <param name="startPosition">The initial subscription position.</param>
        /// <exception cref="ArgumentOutOfRangeException">
        /// Thrown when <paramref name="startPosition"/> is not a defined <see cref="StreamSubscriptionStartPosition"/> value.
        /// </exception>
        public StreamSubscriptionOptions(StreamSubscriptionStartPosition startPosition)
        {
            Validate(startPosition);
            StartPosition = startPosition;
        }

        /// <summary>
        /// Gets options which begin delivery with messages published after the subscription is established.
        /// </summary>
        public static StreamSubscriptionOptions Latest { get; } = new(StreamSubscriptionStartPosition.Latest);

        /// <summary>
        /// Gets options which begin delivery at the earliest message for the stream currently retained in the local queue cache.
        /// </summary>
        public static StreamSubscriptionOptions EarliestAvailable { get; } = new(StreamSubscriptionStartPosition.EarliestAvailable);

        /// <summary>
        /// Gets the initial subscription position.
        /// </summary>
        public StreamSubscriptionStartPosition StartPosition { get; }

        internal void Validate() => Validate(StartPosition);

        private static void Validate(StreamSubscriptionStartPosition startPosition)
        {
            if (startPosition is not StreamSubscriptionStartPosition.Latest and not StreamSubscriptionStartPosition.EarliestAvailable)
            {
                throw new ArgumentOutOfRangeException(nameof(startPosition), startPosition, "The subscription start position is not defined.");
            }
        }
    }

    /// <summary>
    /// Specifies the initial position of a rewindable persistent stream subscription.
    /// </summary>
    public enum StreamSubscriptionStartPosition
    {
        /// <summary>
        /// Begins with messages published after the subscription is established.
        /// </summary>
        Latest,

        /// <summary>
        /// Begins inclusively at the earliest message for the target stream currently retained in the local queue cache.
        /// When the cache has no retained message for the stream, delivery begins with its first future message.
        /// </summary>
        EarliestAvailable,
    }
}
