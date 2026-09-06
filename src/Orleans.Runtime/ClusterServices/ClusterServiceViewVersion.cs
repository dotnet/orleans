using System.Globalization;

namespace Orleans.Runtime.ClusterServices;

/// <summary>
/// An ordered revision within one service's configured view authority.
/// </summary>
[GenerateSerializer, Immutable, Alias(nameof(ClusterServiceViewVersion))]
internal readonly record struct ClusterServiceViewVersion([property: Id(0)] long Value)
    : IComparable<ClusterServiceViewVersion>
{
    public static ClusterServiceViewVersion MinValue => new(long.MinValue);

    public int CompareTo(ClusterServiceViewVersion other) => Value.CompareTo(other.Value);

    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);

    public static bool operator <(ClusterServiceViewVersion left, ClusterServiceViewVersion right) => left.Value < right.Value;
    public static bool operator >(ClusterServiceViewVersion left, ClusterServiceViewVersion right) => left.Value > right.Value;
    public static bool operator <=(ClusterServiceViewVersion left, ClusterServiceViewVersion right) => left.Value <= right.Value;
    public static bool operator >=(ClusterServiceViewVersion left, ClusterServiceViewVersion right) => left.Value >= right.Value;
}
