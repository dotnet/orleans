namespace Orleans.Runtime.ClusterServices;

/// <summary>
/// Identifies a membership-derived view within a logical service.
/// Revisions are ordered within the configured provider epoch.
/// </summary>
[GenerateSerializer, Immutable, Alias("ClusterServiceViewIdentity")]
internal readonly record struct ClusterServiceViewId(
    [property: Id(0)] long ProviderEpoch,
    [property: Id(1)] ClusterServiceViewVersion Version) : IComparable<ClusterServiceViewId>
{
    public int CompareTo(ClusterServiceViewId other)
    {
        if (ProviderEpoch != other.ProviderEpoch)
        {
            throw new InvalidOperationException("Comparing different provider epochs requires an explicit authority migration.");
        }

        return Version.CompareTo(other.Version);
    }

    public static bool operator <(ClusterServiceViewId left, ClusterServiceViewId right) => left.CompareTo(right) < 0;
    public static bool operator >(ClusterServiceViewId left, ClusterServiceViewId right) => left.CompareTo(right) > 0;
    public static bool operator <=(ClusterServiceViewId left, ClusterServiceViewId right) => left.CompareTo(right) <= 0;
    public static bool operator >=(ClusterServiceViewId left, ClusterServiceViewId right) => left.CompareTo(right) >= 0;
}
