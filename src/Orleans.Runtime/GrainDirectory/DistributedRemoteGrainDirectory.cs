using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans.GrainDirectory;

#nullable enable
namespace Orleans.Runtime.GrainDirectory;

/// <summary>
/// A system target that implements <see cref="IRemoteGrainDirectory"/> by delegating to <see cref="DistributedGrainDirectory"/>.
/// This enables silos running the old <see cref="LocalGrainDirectory"/> to forward directory requests to silos running the
/// new <see cref="DistributedGrainDirectory"/> during a rolling upgrade.
/// </summary>
internal sealed class DistributedRemoteGrainDirectory : SystemTarget, IRemoteGrainDirectory
{
    private readonly DistributedGrainDirectory _directory;

    private DistributedRemoteGrainDirectory(
        DistributedGrainDirectory directory,
        GrainType grainType,
        SystemTargetShared shared)
        : base(grainType, shared)
    {
        _directory = directory;
        shared.ActivationDirectory.RecordNewTarget(this);
    }

    /// <summary>
    /// Creates the pair of system targets that replace <see cref="RemoteGrainDirectory"/> when
    /// <see cref="DistributedGrainDirectory"/> is active: one for <see cref="Constants.DirectoryServiceType"/>
    /// and one for <see cref="Constants.DirectoryCacheValidatorType"/>.
    /// </summary>
    internal static (DistributedRemoteGrainDirectory DirectoryService, DistributedRemoteGrainDirectory CacheValidator)
        Create(DistributedGrainDirectory directory, SystemTargetShared shared)
    {
        var directoryService = new DistributedRemoteGrainDirectory(directory, Constants.DirectoryServiceType, shared);
        var cacheValidator = new DistributedRemoteGrainDirectory(directory, Constants.DirectoryCacheValidatorType, shared);
        return (directoryService, cacheValidator);
    }

    public async Task<AddressAndTag> RegisterAsync(GrainAddress address, int hopCount)
    {
        var result = await _directory.Register(address);
        return new(result, 0);
    }

    public async Task<AddressAndTag> RegisterAsync(GrainAddress address, GrainAddress? previousAddress, int hopCount)
    {
        var result = await _directory.Register(address, previousAddress);
        return new(result, 0);
    }

    public async Task<AddressAndTag> LookupAsync(GrainId grainId, int hopCount)
    {
        var result = await _directory.Lookup(grainId);
        return new(result, 0);
    }

    public async Task UnregisterAsync(GrainAddress address, UnregistrationCause cause, int hopCount)
    {
        await _directory.Unregister(address);
    }

    public async Task UnregisterManyAsync(List<GrainAddress> addresses, UnregistrationCause cause, int hopCount)
    {
        foreach (var address in addresses)
        {
            await _directory.Unregister(address);
        }
    }

    public async Task DeleteGrainAsync(GrainId grainId, int hopCount)
    {
        var existing = await _directory.Lookup(grainId);
        if (existing is not null)
        {
            await _directory.Unregister(existing);
        }
    }

    public async Task RegisterMany(List<GrainAddress> addresses)
    {
        foreach (var address in addresses)
        {
            await _directory.Register(address);
        }
    }

    public async Task<List<AddressAndTag>> LookUpMany(List<(GrainId GrainId, int Version)> grainAndETagList)
    {
        var result = new List<AddressAndTag>(grainAndETagList.Count);
        foreach (var (grainId, _) in grainAndETagList)
        {
            var address = await _directory.Lookup(grainId);
            result.Add(new(address, 0));
        }

        return result;
    }

    public async Task AcceptSplitPartition(List<GrainAddress> singleActivations)
    {
        foreach (var address in singleActivations)
        {
            await _directory.Register(address);
        }
    }
}
