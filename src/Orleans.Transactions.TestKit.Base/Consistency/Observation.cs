using System;
using System.Collections.Generic;

namespace Orleans.Transactions.TestKit.Consistency
{
    /// <summary>
    /// Describes a transactional read observation of a grain state version.
    /// </summary>
    [Serializable]
    [GenerateSerializer]
    public struct Observation : IEquatable<Observation>
    {
        /// <summary>
        /// Gets or sets the logical number of the observed grain.
        /// </summary>
        [Id(0)]
        public int Grain { get; set; }

        /// <summary>
        /// Gets or sets the observed state version sequence number.
        /// </summary>
        [Id(1)]
        public int SeqNo { get; set; }

        /// <summary>
        /// Gets or sets the identifier of the transaction which wrote the observed version.
        /// </summary>
        [Id(2)]
        public string WriterTx { get; set; }

        /// <summary>
        /// Gets or sets the identifier of the transaction which made the observation.
        /// </summary>
        [Id(3)]
        public string ExecutingTx { get; set; }

        /// <inheritdoc/>
        public readonly bool Equals(Observation other) =>
            Grain == other.Grain
            && SeqNo == other.SeqNo
            && EqualityComparer<string>.Default.Equals(WriterTx, other.WriterTx)
            && EqualityComparer<string>.Default.Equals(ExecutingTx, other.ExecutingTx);

        /// <inheritdoc/>
        public override readonly bool Equals(object? obj) => obj is Observation other && Equals(other);

        /// <inheritdoc/>
        public override readonly int GetHashCode() => HashCode.Combine(Grain, SeqNo, WriterTx, ExecutingTx);

        /// <summary>
        /// Determines whether two observations are equal.
        /// </summary>
        public static bool operator ==(Observation left, Observation right) => left.Equals(right);

        /// <summary>
        /// Determines whether two observations are unequal.
        /// </summary>
        public static bool operator !=(Observation left, Observation right) => !left.Equals(right);
    }
}
