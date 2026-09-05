using System;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Metadata;
using Orleans.Runtime;
using Orleans.Runtime.Versions;

namespace Orleans;

/// <summary>
/// Provides asynchronous access to grain types which are available in the cluster.
/// </summary>
public interface IGrainTypeAvailability
{
    /// <summary>
    /// Waits until the cluster contains a compatible grain implementation and interface version.
    /// </summary>
    /// <param name="grainInterfaceType">The grain interface type.</param>
    /// <param name="grainClassNamePrefix">An optional grain implementation class name prefix.</param>
    /// <param name="cancellationToken">A token which cancels the wait.</param>
    /// <returns>The available grain type.</returns>
    ValueTask<GrainType> WaitForGrainTypeAsync(
        Type grainInterfaceType,
        string? grainClassNamePrefix = null,
        CancellationToken cancellationToken = default);
}

internal sealed class GrainTypeAvailability(
    GrainInterfaceTypeResolver interfaceTypeResolver,
    GrainInterfaceTypeToGrainTypeResolver grainTypeResolver,
    GrainVersionManifest versionManifest) : IGrainTypeAvailability
{
    public ValueTask<GrainType> WaitForGrainTypeAsync(
        Type grainInterfaceType,
        string? grainClassNamePrefix = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(grainInterfaceType);
        var interfaceType = interfaceTypeResolver.GetGrainInterfaceType(grainInterfaceType);
        var interfaceVersion = versionManifest.GetLocalVersion(interfaceType);
        return grainTypeResolver.WaitForGrainTypeAsync(
            interfaceType,
            interfaceVersion,
            grainClassNamePrefix,
            versionManifest,
            cancellationToken);
    }
}
