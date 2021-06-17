using System;
using System.Collections.Generic;

namespace Orleans.Serialization.TypeSystem
{
    public readonly struct QualifiedType
    {
        public static QualifiedTypeEqualityComparer EqualityComparer { get; } = new QualifiedTypeEqualityComparer();

        public QualifiedType(string assembly, string type)
        {
            Assembly = assembly;
            Type = type;
        }

        public string Assembly { get; }
        public string Type { get; }

        public void Deconstruct(out string assembly, out string type)
        {
            assembly = Assembly;
            type = Type;
        }

        public override bool Equals(object obj) => obj is QualifiedType type && string.Equals(Assembly, type.Assembly, StringComparison.Ordinal) && string.Equals(Type, type.Type, StringComparison.Ordinal);

        public override int GetHashCode() => HashCode.Combine(Assembly, Type);

        public static implicit operator QualifiedType((string Assembly, string Type) args) => new(args.Assembly, args.Type);

        public static bool operator ==(QualifiedType left, QualifiedType right)
        {
            return left.Equals(right);
        }

        public static bool operator !=(QualifiedType left, QualifiedType right)
        {
            return !(left == right);
        }

        public sealed class QualifiedTypeEqualityComparer : IEqualityComparer<QualifiedType>
        {
            public bool Equals(QualifiedType x, QualifiedType y) => x == y;
            public int GetHashCode(QualifiedType obj) => obj.GetHashCode();
        }
    }
}