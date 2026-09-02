namespace Orleans.Runtime;

internal sealed class ClusterOwnershipAccessor : IClusterOwnershipAccessor
{
    public ClusterDirectoryEntry? Current
        => RuntimeContext.Current?.GetComponent(typeof(ClusterDirectoryEntry)) as ClusterDirectoryEntry;
}
