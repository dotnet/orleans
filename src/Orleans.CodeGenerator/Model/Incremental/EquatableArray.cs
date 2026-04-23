using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace Orleans.CodeGenerator.Model.Incremental
{
    /// <summary>
    /// An immutable array wrapper with structural (element-wise) equality semantics,
    /// suitable for use in incremental generator pipeline models.
    /// </summary>
    /// <typeparam name="T">The element type. Must implement <see cref="IEquatable{T}"/>.</typeparam>
    internal readonly struct EquatableArray<T> : IEquatable<EquatableArray<T>>, IReadOnlyList<T>
        where T : IEquatable<T>
    {
        private readonly ImmutableArray<T> _array;

        public EquatableArray(ImmutableArray<T> array) => _array = array;

        public EquatableArray(IEnumerable<T> items) => _array = items.ToImmutableArray();

        public static EquatableArray<T> Empty { get; } = new EquatableArray<T>(ImmutableArray<T>.Empty);

        public ImmutableArray<T> AsImmutableArray() => _array.IsDefault ? ImmutableArray<T>.Empty : _array;

        public int Count => _array.IsDefault ? 0 : _array.Length;

        public T this[int index] => _array[index];

        public bool Equals(EquatableArray<T> other)
        {
            var left = AsImmutableArray();
            var right = other.AsImmutableArray();

            if (left.Length != right.Length)
            {
                return false;
            }

            for (var i = 0; i < left.Length; i++)
            {
                if (!left[i].Equals(right[i]))
                {
                    return false;
                }
            }

            return true;
        }

        public override bool Equals(object obj) => obj is EquatableArray<T> other && Equals(other);

        public override int GetHashCode()
        {
            var array = AsImmutableArray();
            unchecked
            {
                var hash = 17;
                foreach (var item in array)
                {
                    hash = hash * 31 + item.GetHashCode();
                }
                return hash;
            }
        }

        public static bool operator ==(EquatableArray<T> left, EquatableArray<T> right) => left.Equals(right);
        public static bool operator !=(EquatableArray<T> left, EquatableArray<T> right) => !left.Equals(right);

        public ImmutableArray<T>.Enumerator GetEnumerator() => AsImmutableArray().GetEnumerator();
        IEnumerator<T> IEnumerable<T>.GetEnumerator() => ((IEnumerable<T>)AsImmutableArray()).GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => ((IEnumerable)AsImmutableArray()).GetEnumerator();
    }
}
