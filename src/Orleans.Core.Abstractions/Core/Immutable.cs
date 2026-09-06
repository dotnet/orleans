using System;
using System.Collections.Generic;

namespace Orleans.Concurrency
{
    /// <summary>
    /// Wrapper class for carrying immutable data.
    /// </summary>
    /// <remarks>
    /// Objects that are known to be immutable are given special fast-path handling by the Orleans serializer 
    /// -- which in a nutshell allows the DeepCopy step to be skipped during message sends where the sender and receiver grain are in the same silo.
    /// 
    /// One very common usage pattern for Immutable is when passing byte[] parameters to a grain. 
    /// If a program knows it will not alter the contents of the byte[] (for example, if it contains bytes from a static image file read from disk)
    /// then considerable savings in memory usage and message throughput can be obtained by marking that byte[] argument as <c>Immutable</c>.
    /// </remarks>
    /// <typeparam name="T">Type of data to be wrapped by this Immutable</typeparam>
    [GenerateSerializer, Immutable]
    public readonly struct Immutable<T> : IEquatable<Immutable<T>>
    {
        /// <summary> Return reference to the original value stored in this Immutable wrapper. </summary>
        [Id(0)]
        public readonly T Value;

        /// <summary>
        /// Constructor to wrap the specified data object in new Immutable wrapper.
        /// </summary>
        /// <param name="value">Value to be wrapped and marked as immutable.</param>
        public Immutable(T value) => Value = value;

        /// <inheritdoc/>
        public bool Equals(Immutable<T> other) => EqualityComparer<T>.Default.Equals(Value, other.Value);

        /// <inheritdoc/>
        public override bool Equals(object? obj) => obj is Immutable<T> other && Equals(other);

        /// <inheritdoc/>
        public override int GetHashCode() => EqualityComparer<T>.Default.GetHashCode(Value!);

        /// <summary>
        /// Determines whether two immutable wrappers contain equal values.
        /// </summary>
        public static bool operator ==(Immutable<T> left, Immutable<T> right) => left.Equals(right);

        /// <summary>
        /// Determines whether two immutable wrappers contain unequal values.
        /// </summary>
        public static bool operator !=(Immutable<T> left, Immutable<T> right) => !left.Equals(right);
    }

    /// <summary>
    /// Utility class to add the .AsImmutable method to all objects.
    /// </summary>
    public static class ImmutableExtensions
    {
        /// <summary>
        /// Extension method to return this value wrapped in <c>Immutable</c>.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="value">Value to be wrapped.</param>
        /// <returns>Immutable wrapper around the original object.</returns>
        /// <seealso cref="Immutable{T}"/>"/>
        public static Immutable<T> AsImmutable<T>(this T value) => new(value);
    }
}
