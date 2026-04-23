using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans.Concurrency;
using Orleans.GrainDirectory;

namespace Orleans.Runtime.GrainDirectory;

/// <summary>
/// Compatibility system targets which allow distributed-directory silos to interact with local-directory silos
/// during rolling upgrades.
/// </summary>
internal sealed class LocalGrainDirectoryClientCompatibility : SystemTarget, IGrainDirectoryClient
{
    private readonly LocalGrainDirectory _directory;

    internal LocalGrainDirectoryClientCompatibility(LocalGrainDirectory directory, SystemTargetShared shared)
        : base(Constants.GrainDirectoryType, shared)
    {
        _directory = directory;
        shared.ActivationDirectory.RecordNewTarget(this);
    }

    public ValueTask<Immutable<List<GrainAddress>>> GetRegisteredActivations(MembershipVersion membershipVersion, RingRange range, bool isValidation)
        => new(_directory.DirectoryPartition.Split(range.Contains).AsImmutable());

    public ValueTask<Immutable<List<GrainAddress>>> RecoverRegisteredActivations(MembershipVersion membershipVersion, RingRange range, SiloAddress siloAddress, int partitionId)
        => GetRegisteredActivations(membershipVersion, range, isValidation: false);
}

internal sealed class LocalGrainDirectoryPartitionCompatibility : SystemTarget, IGrainDirectoryPartition
{
    private readonly LocalGrainDirectory _directory;

    internal LocalGrainDirectoryPartitionCompatibility(LocalGrainDirectory directory, SystemTargetShared shared)
        : base(GrainDirectoryPartition.CreateGrainId(shared.SiloAddress, 0), shared)
    {
        _directory = directory;
        shared.ActivationDirectory.RecordNewTarget(this);
    }

    public async ValueTask<DirectoryResult<GrainAddress>> RegisterAsync(MembershipVersion version, GrainAddress address, GrainAddress? currentRegistration)
    {
        var result = await _directory.RegisterAsync(address, currentRegistration, hopCount: 1);
        return DirectoryResult.FromResult(result.Address!, version);
    }

    public async ValueTask<DirectoryResult<GrainAddress?>> LookupAsync(MembershipVersion version, GrainId grainId)
    {
        var result = await _directory.LookupAsync(grainId, hopCount: 1);
        return DirectoryResult.FromResult(result.Address, version);
    }

    public async ValueTask<DirectoryResult<bool>> DeregisterAsync(MembershipVersion version, GrainAddress address)
    {
        await _directory.UnregisterAsync(address, UnregistrationCause.Force, hopCount: 1);
        return DirectoryResult.FromResult(true, version);
    }

    public ValueTask<GrainDirectoryPartitionSnapshot?> GetSnapshotAsync(MembershipVersion version, MembershipVersion rangeVersion, RingRange range)
        => new(new GrainDirectoryPartitionSnapshot(rangeVersion, _directory.DirectoryPartition.Split(range.Contains)));

    public ValueTask<bool> AcknowledgeSnapshotTransferAsync(SiloAddress silo, int partitionIndex, MembershipVersion version) => new(true);
}
