using System;
using System.Collections.Generic;

namespace Orleans.Serialization.TypeSystem
{
    /// <summary>
    /// Represents an assembly-qualifies type.
    /// </summary>
    public readonly struct QualifiedType
    {
        /// <summary>
        /// Gets the equality comparer.
        /// </summary>
        /// <value>The equality comparer.</value>
        public static QualifiedTypeEqualityComparer EqualityComparer { get; } = new QualifiedTypeEqualityComparer();

        /// <summary>
        /// Initializes a new instance of the <see cref="QualifiedType"/> struct.
        /// </summary>
        /// <param name="assembly">The assembly.</param>
        /// <param name="type">The type.</param>
        public QualifiedType(string assembly, string type)
        {
            Assembly = assembly;
            Type = type;
        }

        /// <summary>
        /// Gets the assembly.
        /// </summary>
        /// <value>The assembly.</value>
        public string Assembly { get; }

        /// <summary>
        /// Gets the type.
        /// </summary>
        /// <value>The type.</value>
        public string Type { get; }

        /// <summary>
        /// Deconstructs this instance.
        /// </summary>
        /// <param name="assembly">The assembly.</param>
        /// <param name="type">The type.</param>
        public void Deconstruct(out string assembly, out string type)
        {
            assembly = Assembly;
            type = Type;
        }

        /// <inheritdoc/>
        public override bool Equals(object obj) => obj is QualifiedType type && string.Equals(Assembly, type.Assembly, StringComparison.Ordinal) && string.Equals(Type, type.Type, StringComparison.Ordinal);

        /// <inheritdoc/>
        public override int GetHashCode() => HashCode.Combine(Assembly, Type);

        /// <summary>
        /// Performs an implicit conversion from <see cref="System.ValueTuple{T1, T2}"/> to <see cref="QualifiedType"/>.
        /// </summary>
        /// <param name="args">The arguments.</param>
        /// <returns>The result of the conversion.</returns>
        public static implicit operator QualifiedType((string Assembly, string Type) args) => new(args.Assembly, args.Type);

        /// <summary>
        /// Compares two values for equality.
        /// </summary>
        /// <param name="left">The left.</param>
        /// <param name="right">The right.</param>
        /// <returns>The result of the operator.</returns>
        public static bool operator ==(QualifiedType left, QualifiedType right)
        {
            return left.Equals(right);
        }

        /// <summary>
        /// Compares two values for inequality.
        /// </summary>
        /// <param name="left">The left.</param>
        /// <param name="right">The right.</param>
        /// <returns>The result of the operator.</returns>
        public static bool operator !=(QualifiedType left, QualifiedType right)
        {
            return !(left == right);
        }

        /// <summary>
        /// Equality comparer for <see cref="QualifiedType"/>.
        /// </summary>
        public sealed class QualifiedTypeEqualityComparer : IEqualityComparer<QualifiedType>
        {
            /// <inheritdoc/>
            public bool Equals(QualifiedType x, QualifiedType y) => x == y;

            /// <inheritdoc/>
            public int GetHashCode(QualifiedType obj) => obj.GetHashCode();
        }
    }
}