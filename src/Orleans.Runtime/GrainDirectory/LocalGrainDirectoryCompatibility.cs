using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Concurrency;
using Orleans.GrainDirectory;

namespace Orleans.Runtime.GrainDirectory;

/// <summary>
/// Compatibility system targets which allow distributed-directory silos to interact with local-directory silos
/// during rolling upgrades.
/// </summary>
internal sealed class LocalGrainDirectoryClientCompatibility : SystemTarget, IGrainDirectoryClient
{
    private readonly ActivationDirectory _localActivations;
    private GrainDirectoryResolver? _grainDirectoryResolver;

    internal LocalGrainDirectoryClientCompatibility(SystemTargetShared shared)
        : base(Constants.GrainDirectoryType, shared)
    {
        _localActivations = shared.ActivationDirectory;
        shared.ActivationDirectory.RecordNewTarget(this);
    }

    public ValueTask<Immutable<List<GrainAddress>>> GetRegisteredActivations(MembershipVersion membershipVersion, RingRange range, bool isValidation)
    {
        var grainDirectoryResolver = _grainDirectoryResolver ??= ActivationServices.GetRequiredService<GrainDirectoryResolver>();
        List<GrainAddress> result = [];
        foreach (var (_, activation) in _localActivations)
        {
            if (!UsesLocalGrainDirectory(activation, grainDirectoryResolver))
            {
                continue;
            }

            if (activation is ActivationData activationData && !activationData.IsValid)
            {
                continue;
            }

            var address = activation.Address;
            if (range.Contains(address.GrainId))
            {
                result.Add(address);
            }
        }

        return new(result.AsImmutable());
    }

    private static bool UsesLocalGrainDirectory(IGrainContext activation, GrainDirectoryResolver grainDirectoryResolver)
    {
        if (activation is ActivationData activationData)
        {
            return activationData.IsUsingGrainDirectory && activationData.Shared.GrainDirectory is null;
        }
        else if (activation is SystemTarget)
        {
            return false;
        }
        else if (activation.GetComponent<PlacementStrategy>() is { IsUsingGrainDirectory: true })
        {
            return DistributedGrainDirectory.GetGrainDirectory(activation, grainDirectoryResolver) is null;
        }

        return false;
    }

    public ValueTask<Immutable<List<GrainAddress>>> RecoverRegisteredActivations(MembershipVersion membershipVersion, RingRange range, SiloAddress siloAddress, int partitionId)
        => GetRegisteredActivations(membershipVersion, range, isValidation: false);
}

internal sealed class LocalGrainDirectoryPartitionCompatibility : SystemTarget, IGrainDirectoryPartition
{
    private readonly LocalGrainDirectory _directory;

    internal LocalGrainDirectoryPartitionCompatibility(LocalGrainDirectory directory, SystemTargetShared shared, int partitionIndex)
        : base(GrainDirectoryPartition.CreateGrainId(shared.SiloAddress, partitionIndex), shared)
    {
        _directory = directory;
        shared.ActivationDirectory.RecordNewTarget(this);
    }

    public ValueTask<DirectoryResult<GrainAddress>> RegisterAsync(MembershipVersion version, GrainAddress address, GrainAddress? currentRegistration)
    {
        var result = _directory.DirectoryPartition.AddSingleActivation(address, currentRegistration);
        return new(DirectoryResult.FromResult(result.Address!, version));
    }

    public ValueTask<DirectoryResult<GrainAddress?>> LookupAsync(MembershipVersion version, GrainId grainId)
    {
        var result = _directory.DirectoryPartition.LookUpActivation(grainId);
        return new(DirectoryResult.FromResult<GrainAddress?>(result.Address, version));
    }

    public ValueTask<DirectoryResult<bool>> DeregisterAsync(MembershipVersion version, GrainAddress address)
    {
        _directory.DirectoryPartition.RemoveActivation(address.GrainId, address.ActivationId, UnregistrationCause.Force);
        return new(DirectoryResult.FromResult(true, version));
    }

    public ValueTask<GrainDirectoryPartitionSnapshot?> GetSnapshotAsync(MembershipVersion version, MembershipVersion rangeVersion, RingRange range)
    {
        // LocalGrainDirectory stores entries using the legacy single-ring ownership scheme, so this local
        // partition cannot produce a complete snapshot for a distributed virtual partition range.
        return new((GrainDirectoryPartitionSnapshot?)null);
    }

    public ValueTask<bool> AcknowledgeSnapshotTransferAsync(SiloAddress silo, int partitionIndex, MembershipVersion version) => new(true);
}
