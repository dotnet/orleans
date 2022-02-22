using System;
using System.Collections.Generic;

namespace Orleans.Runtime
{
    /// <summary>
    /// Identifies a client-side observer object.
    /// </summary>
    internal readonly struct ObserverGrainId : IEquatable<ObserverGrainId>, IComparable<ObserverGrainId>
    {
        /// <summary>
        /// The separator between the client id portion of the observer id and the client-scoped observer id portion.
        /// </summary>
        internal const char SegmentSeparator = '+';

        /// <summary>
        /// Initializes a new instance of the <see cref="ObserverGrainId"/> struct.
        /// </summary>
        private ObserverGrainId(GrainId grainId)
        {
            this.GrainId = grainId;
        }

        /// <summary>
        /// Gets the underlying <see cref="GrainId"/>.
        /// </summary>
        public GrainId GrainId { get; }

        /// <summary>
        /// Returns the <see cref="ClientGrainId"/> associated with this instance.
        /// </summary>
        /// <returns>
        /// The <see cref="ClientGrainId"/> associated with this instance.
        /// </returns>
        public ClientGrainId GetClientId()
        {
            if (!ClientGrainId.TryParse(this.GrainId, out var result))
            {
                static void ThrowInvalidGrainId(GrainId grainId) => throw new InvalidOperationException($"GrainId {grainId} cannot be converted to a {nameof(ClientGrainId)}");
                ThrowInvalidGrainId(this.GrainId);
            }

            return result;
        }

        /// <summary>
        /// Returns a new, random <see cref="ObserverGrainId"/> instance for the provided client id.
        /// </summary>
        /// <param name="clientId">
        /// The client id.
        /// </param>
        /// <returns>
        /// A new, random <see cref="ObserverGrainId"/> instance for the provided client id.
        /// </returns>
        public static ObserverGrainId Create(ClientGrainId clientId) => Create(clientId, GrainIdKeyExtensions.CreateGuidKey(Guid.NewGuid()));

        /// <summary>
        /// Returns a new <see cref="ObserverGrainId"/> instance for the provided client id.
        /// </summary>
        /// <param name="clientId">
        /// The client id.
        /// </param>
        /// <param name="scopedId">
        /// The client-scoped observer id.
        /// </param>
        /// <returns>
        /// A new <see cref="ObserverGrainId"/> instance for the provided client id.
        /// </returns>
        public static ObserverGrainId Create(ClientGrainId clientId, IdSpan scopedId) => new ObserverGrainId(ConstructGrainId(clientId, scopedId));

        /// <summary>
        /// Returns <see langword="true"/> if the provided instance represents an observer, <see langword="false"/> if otherwise.
        /// </summary>
        /// <param name="grainId">
        /// The grain id.
        /// </param>
        /// <returns>
        /// <see langword="true"/> if the provided grain id is an observer id, otherwise <see langword="false"/>.
        /// </returns>
        public static bool IsObserverGrainId(GrainId grainId) => grainId.IsClient() && grainId.Key.AsSpan().IndexOf((byte)SegmentSeparator) >= 0;

        /// <summary>
        /// Converts the provided <see cref="GrainId"/> to a <see cref="ObserverGrainId"/>. A return value indicates whether the operation succeeded.
        /// </summary>
        /// <param name="grainId">
        /// The grain id.
        /// </param>
        /// <param name="observerId">
        /// The corresponding observer id.
        /// </param>
        /// <returns>
        /// <see langword="true"/> if the provided grain id is an observer id, otherwise <see langword="false"/>.
        /// </returns>
        public static bool TryParse(GrainId grainId, out ObserverGrainId observerId)
        {
            if (!IsObserverGrainId(grainId))
            {
                observerId = default;
                return false;
            }

            observerId = new ObserverGrainId(grainId);
            return true;
        }

        private static GrainId ConstructGrainId(ClientGrainId clientId, IdSpan scopedId)
        {
            var grain = clientId.GrainId.Key.AsSpan();
            var scope = scopedId.AsSpan();

            var buf = new byte[grain.Length + 1 + scope.Length];
            grain.CopyTo(buf);
            buf[grain.Length] = (byte)SegmentSeparator;
            scope.CopyTo(buf.AsSpan(grain.Length + 1));

            return GrainId.Create(clientId.GrainId.Type, new IdSpan(buf));
        }

        /// <inheritdoc/>
        public bool Equals(ObserverGrainId other) => this.GrainId.Equals(other.GrainId);

        /// <inheritdoc/>
        public override bool Equals(object obj) => obj is ObserverGrainId observer && this.Equals(observer);

        /// <inheritdoc/>
        public override int GetHashCode() => this.GrainId.GetHashCode();

        /// <inheritdoc/>
        public override string ToString() => this.GrainId.ToString();

        /// <inheritdoc/>
        public int CompareTo(ObserverGrainId other) => this.GrainId.CompareTo(other.GrainId);

        /// <summary>
        /// Compares the provided operands for equality.
        /// </summary>
        /// <param name="left">The left operand.</param>
        /// <param name="right">The right operand.</param>
        /// <returns><see langword="true"/> if the provided values are equal, otherwise <see langword="false"/>.</returns>
        public static bool operator ==(ObserverGrainId left, ObserverGrainId right) => left.Equals(right);

        /// <summary>
        /// Compares the provided operands for inequality.
        /// </summary>
        /// <param name="left">The left operand.</param>
        /// <param name="right">The right operand.</param>
        /// <returns><see langword="true"/> if the provided values are not equal, otherwise <see langword="false"/>.</returns>
        public static bool operator !=(ObserverGrainId left, ObserverGrainId right) => !(left == right);

        /// <summary>
        /// Compares the provided operands and returns <see langword="true"/> if the left operand is less than the right operand, otherwise <see langword="false"/>.
        /// </summary>
        /// <param name="left">The left operand.</param>
        /// <param name="right">The right operand.</param>
        /// <returns><see langword="true"/> if the left operand is less than the right operand, otherwise <see langword="false"/>.</returns>
        public static bool operator <(ObserverGrainId left, ObserverGrainId right) => left.CompareTo(right) < 0;

        /// <summary>
        /// Compares the provided operands and returns <see langword="true"/> if the left operand is less than or equal to the right operand, otherwise <see langword="false"/>.
        /// </summary>
        /// <param name="left">The left operand.</param>
        /// <param name="right">The right operand.</param>
        /// <returns><see langword="true"/> if the left operand is less than or equal to the right operand, otherwise <see langword="false"/>.</returns>
        public static bool operator <=(ObserverGrainId left, ObserverGrainId right) => left.CompareTo(right) <= 0;

        /// <summary>
        /// Compares the provided operands and returns <see langword="true"/> if the left operand is greater than the right operand, otherwise <see langword="false"/>.
        /// </summary>
        /// <param name="left">The left operand.</param>
        /// <param name="right">The right operand.</param>
        /// <returns><see langword="true"/> if the left operand is greater than the right operand, otherwise <see langword="false"/>.</returns>
        public static bool operator >(ObserverGrainId left, ObserverGrainId right) => left.CompareTo(right) > 0;

        /// <summary>
        /// Compares the provided operands and returns <see langword="true"/> if the left operand is greater than or equal to the right operand, otherwise <see langword="false"/>.
        /// </summary>
        /// <param name="left">The left operand.</param>
        /// <param name="right">The right operand.</param>
        /// <returns><see langword="true"/> if the left operand is greater than or equal to the right operand, otherwise <see langword="false"/>.</returns>
        public static bool operator >=(ObserverGrainId left, ObserverGrainId right) => left.CompareTo(right) >= 0;

        /// <summary>
        /// An <see cref="IEqualityComparer{T}"/> and <see cref="IComparer{T}"/> implementation for <see cref="ObserverGrainId"/>.
        /// </summary>
        public sealed class Comparer : IEqualityComparer<ObserverGrainId>, IComparer<ObserverGrainId>
        {
            /// <summary>
            /// Gets the singleton <see cref="Comparer"/> instance.
            /// </summary>
            public static Comparer Instance { get; } = new Comparer();

            /// <inheritdoc/>
            public int Compare(ObserverGrainId x, ObserverGrainId y) => x.CompareTo(y);

            /// <inheritdoc/>
            public bool Equals(ObserverGrainId x, ObserverGrainId y) => x.Equals(y);

            /// <inheritdoc/>
            public int GetHashCode(ObserverGrainId obj) => obj.GetHashCode();
        }
    }
}
