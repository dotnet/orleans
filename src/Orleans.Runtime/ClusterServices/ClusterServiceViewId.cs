namespace Orleans.Runtime.ClusterServices;

/// <summary>
/// Identifies a view within a logical service. Provider epochs order changes of authority;
/// revisions are ordered within an epoch.
/// </summary>
[GenerateSerializer, Immutable, Alias("ClusterServiceViewIdentity")]
internal readonly record struct ClusterServiceViewId(
    [property: Id(0)] long ProviderEpoch,
    [property: Id(1)] ClusterServiceViewVersion Version) : IComparable<ClusterServiceViewId>
{
    public int CompareTo(ClusterServiceViewId other)
    {
        var epoch = ProviderEpoch.CompareTo(other.ProviderEpoch);
        return epoch != 0 ? epoch : Version.CompareTo(other.Version);
    }

    public static bool operator <(ClusterServiceViewId left, ClusterServiceViewId right) => left.CompareTo(right) < 0;
    public static bool operator >(ClusterServiceViewId left, ClusterServiceViewId right) => left.CompareTo(right) > 0;
    public static bool operator <=(ClusterServiceViewId left, ClusterServiceViewId right) => left.CompareTo(right) <= 0;
    public static bool operator >=(ClusterServiceViewId left, ClusterServiceViewId right) => left.CompareTo(right) >= 0;
}
