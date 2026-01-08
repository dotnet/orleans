using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

#nullable disable
namespace Orleans.Serialization.Utilities
{
    internal sealed class ReferenceEqualsComparer : IEqualityComparer<object>, IEqualityComparer
    {
        public static ReferenceEqualsComparer Default { get; } = new();

        public new bool Equals(object x, object y) => ReferenceEquals(x, y);

        public int GetHashCode(object obj) => obj is null ? 0 : RuntimeHelpers.GetHashCode(obj);
    }
}
