using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans.Concurrency;

#nullable enable
namespace Orleans.Runtime.GrainDirectory;

[Alias("IGrainDirectoryReplica")]
internal interface IGrainDirectoryReplica : ISystemTarget
{
    [Alias("RegisterAsync")]
    ValueTask<DirectoryResult<GrainAddress>> RegisterAsync(MembershipVersion version, GrainAddress address, GrainAddress? currentRegistration);

    [Alias("LookupAsync")]
    ValueTask<DirectoryResult<GrainAddress?>> LookupAsync(MembershipVersion version, GrainId grainId);

    [Alias("DeregisterAsync")]
    ValueTask<DirectoryResult<bool>> DeregisterAsync(MembershipVersion version, GrainAddress address);

    [Alias("GetSnapshotAsync")]
    ValueTask<GrainDirectoryPartitionSnapshot?> GetSnapshotAsync(MembershipVersion version, MembershipVersion rangeVersion, RingRangeCollection ranges);

    [Alias("AcknowledgeSnapshotTransferAsync")]
    ValueTask<bool> AcknowledgeSnapshotTransferAsync(SiloAddress owner, MembershipVersion version);
}

[Alias("IGrainDirectoryReplicaClient")]
internal interface IGrainDirectoryReplicaClient : ISystemTarget
{
    [Alias("GetRegisteredActivations")]
    ValueTask<Immutable<List<GrainAddress>>> GetRegisteredActivations(MembershipVersion membershipVersion, RingRangeCollection ranges, bool isValidation);
}

[Alias("IGrainDirectoryReplicaTestHooks")]
internal interface IGrainDirectoryReplicaTestHooks : ISystemTarget
{
    [Alias("CheckIntegrityAsync")]
    ValueTask CheckIntegrityAsync();
}
