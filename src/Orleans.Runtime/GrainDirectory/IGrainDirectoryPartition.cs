using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans.Concurrency;

#nullable enable
namespace Orleans.Runtime.GrainDirectory;

[Alias("IGrainDirectoryPartition")]
internal interface IGrainDirectoryPartition : ISystemTarget
{
    [Alias("RegisterAsync")]
    ValueTask<DirectoryResult<GrainAddress>> RegisterAsync(MembershipVersion version, GrainAddress address, GrainAddress? currentRegistration);

    [Alias("LookupAsync")]
    ValueTask<DirectoryResult<GrainAddress?>> LookupAsync(MembershipVersion version, GrainId grainId);

    [Alias("DeregisterAsync")]
    ValueTask<DirectoryResult<bool>> DeregisterAsync(MembershipVersion version, GrainAddress address);

    [Alias("GetSnapshotAsync")]
    ValueTask<GrainDirectoryPartitionSnapshot?> GetSnapshotAsync(MembershipVersion version, MembershipVersion rangeVersion, RingRange range);

    [Alias("AcknowledgeSnapshotTransferAsync")]
    ValueTask<bool> AcknowledgeSnapshotTransferAsync(SiloAddress silo, int partitionIndex, MembershipVersion version);
}

[Alias("IGrainDirectoryClient")]
internal interface IGrainDirectoryClient : ISystemTarget
{
    [Alias("GetRegisteredActivations")]
    ValueTask<Immutable<List<GrainAddress>>> GetRegisteredActivations(MembershipVersion membershipVersion, RingRange range, bool isValidation);

    [Alias("RecoverRegisteredActivations")]
    ValueTask<Immutable<List<GrainAddress>>> RecoverRegisteredActivations(MembershipVersion membershipVersion, RingRange range, SiloAddress siloAddress, int partitionId);
}

[Alias("IGrainDirectoryTestHooks")]
internal interface IGrainDirectoryTestHooks : ISystemTarget
{
    [Alias("CheckIntegrityAsync")]
    ValueTask CheckIntegrityAsync();
}
