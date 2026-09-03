
namespace Orleans.Runtime.MembershipService.SiloMetadata;

/// <summary>
/// Provides locally cached metadata for silos in the cluster.
/// </summary>
public interface ISiloMetadataCache
{
    /// <summary>
    /// Gets the cached metadata for the specified silo.
    /// </summary>
    /// <param name="siloAddress">The address of the silo.</param>
    /// <returns>
    /// The metadata for <paramref name="siloAddress"/>, or <see cref="SiloMetadata.Empty"/> when metadata is not available.
    /// </returns>
    SiloMetadata GetSiloMetadata(SiloAddress siloAddress);
}