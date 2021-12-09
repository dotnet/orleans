using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Orleans
{
    internal class ReferenceEqualsComparer : IEqualityComparer<object>
    {
        /// <summary>
        /// Gets an instance of this class.
        /// </summary>
        public static ReferenceEqualsComparer Default { get; } = new ReferenceEqualsComparer();

        /// <summary>
        /// Defines object equality by reference equality (eq, in LISP).
        /// </summary>
        /// <returns>
        /// true if the specified objects are equal; otherwise, false.
        /// </returns>
        /// <param name="x">The first object to compare.</param><param name="y">The second object to compare.</param>
        public new bool Equals(object x, object y) => object.ReferenceEquals(x, y);

        public int GetHashCode(object obj) => obj == null ? 0 : RuntimeHelpers.GetHashCode(obj);
    }

    internal class ReferenceEqualsComparer<T> : IEqualityComparer<T> where T : class
    {
        /// <summary>
        /// Gets an instance of this class.
        /// </summary>
        public static ReferenceEqualsComparer<T> Default { get; } = new ReferenceEqualsComparer<T>();

        /// <summary>
        /// Defines object equality by reference equality (eq, in LISP).
        /// </summary>
        /// <returns>
        /// true if the specified objects are equal; otherwise, false.
        /// </returns>
        /// <param name="x">The first object to compare.</param><param name="y">The second object to compare.</param>
        public bool Equals(T x, T y) => object.ReferenceEquals(x, y);

        public int GetHashCode(T obj) => obj == null ? 0 : RuntimeHelpers.GetHashCode(obj);
    }
}