namespace Orleans.Providers.Streams.Common
{
    /// <summary>
    /// Describes where a recoverable stream source begins reading.
    /// </summary>
    public readonly struct RecoverableStreamStartPosition : IEquatable<RecoverableStreamStartPosition>
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="RecoverableStreamStartPosition"/> struct.
        /// </summary>
        /// <param name="checkpoint">The durable checkpoint, or <see langword="null"/> if one does not exist.</param>
        /// <param name="startFromNow">Whether a source without a checkpoint starts at its current tail.</param>
        public RecoverableStreamStartPosition(string? checkpoint, bool startFromNow)
        {
            Checkpoint = checkpoint;
            StartFromNow = startFromNow;
        }

        /// <summary>
        /// Gets the durable checkpoint. Sources must begin strictly after this value.
        /// </summary>
        public string? Checkpoint { get; }

        /// <summary>
        /// Gets a value indicating whether a source without a checkpoint starts at its current tail.
        /// </summary>
        public bool StartFromNow { get; }

        /// <inheritdoc />
        public bool Equals(RecoverableStreamStartPosition other)
            => string.Equals(Checkpoint, other.Checkpoint, StringComparison.Ordinal)
                && StartFromNow == other.StartFromNow;

        /// <inheritdoc />
        public override bool Equals(object? obj) => obj is RecoverableStreamStartPosition other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode() => HashCode.Combine(Checkpoint, StartFromNow);

        /// <summary>
        /// Determines whether two start positions are equal.
        /// </summary>
        public static bool operator ==(RecoverableStreamStartPosition left, RecoverableStreamStartPosition right) => left.Equals(right);

        /// <summary>
        /// Determines whether two start positions differ.
        /// </summary>
        public static bool operator !=(RecoverableStreamStartPosition left, RecoverableStreamStartPosition right) => !left.Equals(right);
    }
}
