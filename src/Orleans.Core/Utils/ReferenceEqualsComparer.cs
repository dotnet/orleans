using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Orleans
{
    internal class ReferenceEqualsComparer : EqualityComparer<object>
    {
        /// <summary>
        /// Gets an instance of this class.
        /// </summary>
        public static ReferenceEqualsComparer Instance { get; } = new ReferenceEqualsComparer();

        /// <summary>
        /// Defines object equality by reference equality (eq, in LISP).
        /// </summary>
        /// <returns>
        /// true if the specified objects are equal; otherwise, false.
        /// </returns>
        /// <param name="x">The first object to compare.</param><param name="y">The second object to compare.</param>
        public override bool Equals(object x, object y)
        {
            return object.ReferenceEquals(x, y);
        }

        public override int GetHashCode(object obj)
        {
            return obj == null ? 0 : RuntimeHelpers.GetHashCode(obj);
        }
    }

    internal class ReferenceEqualsComparer<T> : EqualityComparer<T> where T : class
    {
        /// <summary>
        /// Gets an instance of this class.
        /// </summary>
        public static ReferenceEqualsComparer<T> Instance { get; } = new ReferenceEqualsComparer<T>();

        /// <summary>
        /// Defines object equality by reference equality (eq, in LISP).
        /// </summary>
        /// <returns>
        /// true if the specified objects are equal; otherwise, false.
        /// </returns>
        /// <param name="x">The first object to compare.</param><param name="y">The second object to compare.</param>
        public override bool Equals(T x, T y)
        {
            return object.ReferenceEquals(x, y);
        }

        public override int GetHashCode(T obj)
        {
            return obj == null ? 0 : RuntimeHelpers.GetHashCode(obj);
        }
    }
}