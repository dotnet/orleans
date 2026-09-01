using System;

namespace Orleans.Streams
{
    /// <summary>
    /// Specifies the initial position of a rewindable persistent stream subscription.
    /// </summary>
    /// <remarks>
    /// During a rolling deployment, use start-position subscriptions after the silos hosting persistent-stream
    /// pulling agents have been upgraded to a version which supports this enum.
    /// </remarks>
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

    internal static class StreamSubscriptionStartPositionExtensions
    {
        public static void Validate(this StreamSubscriptionStartPosition startPosition)
        {
            if (startPosition is not StreamSubscriptionStartPosition.Latest and not StreamSubscriptionStartPosition.EarliestAvailable)
            {
                throw new ArgumentOutOfRangeException(nameof(startPosition), startPosition, "The subscription start position is not defined.");
            }
        }
    }
}
