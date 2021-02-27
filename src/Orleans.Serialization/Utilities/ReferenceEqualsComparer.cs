using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Orleans.Serialization.Utilities
{
    internal sealed class ReferenceEqualsComparer : EqualityComparer<object>
    {
        public override bool Equals(object x, object y) => ReferenceEquals(x, y);
        public override int GetHashCode(object obj) => obj is null ? 0 : RuntimeHelpers.GetHashCode(obj);
    }
}