namespace Orleans.Runtime.Utilities;

/// <summary>Compares grain identities using their stable, cluster-wide hash-ring coordinate.</summary>
internal readonly struct GrainIdUniformHashComparer : IConsistentHashComparer<GrainId>
{
    public static GrainIdUniformHashComparer Instance { get; } = new();

    public bool Equals(GrainId x, GrainId y) => x.Equals(y);

    public uint GetHashCode(GrainId value) => value.GetUniformHashCode();
}
