namespace Orleans.Runtime.Dissemination;

internal static class DisseminationTopicNames
{
    public const string DeploymentLoad = "load";
    public const string Membership = "membership";
    public const string Manifest = "manifest";

    public const string SiloRuntimeStatistics = "SiloRuntimeStatistics";
    public const string MembershipSnapshot = "MembershipSnapshot";
    public const string MembershipSnapshotDiff = "MembershipSnapshotDiff";
    public const string ManifestHash = "ManifestHash";
}
