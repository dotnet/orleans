using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans.Runtime.GrainDirectory;

#nullable enable
namespace Orleans.Runtime;

internal interface IGrainDirectoryReplica : ISystemTarget
{        
    ValueTask<DirectoryResult<GrainAddress>> RegisterAsync(MembershipVersion version, GrainAddress address, GrainAddress? currentRegistration);
    ValueTask<DirectoryResult<List<GrainAddress>>> RegisterAsync(MembershipVersion version, List<GrainAddress> addresses);

    ValueTask<DirectoryResult<GrainAddress?>> LookupAsync(MembershipVersion version, GrainId grainId);
    ValueTask<DirectoryResult<List<GrainAddress?>>> LookupAsync(MembershipVersion version, List<GrainId> grainIds);

    ValueTask<DirectoryResult<bool>> UnregisterAsync(MembershipVersion version, GrainAddress address);
    ValueTask<DirectoryResult<bool>> UnregisterAsync(MembershipVersion version, List<GrainAddress> addresses);

    ValueTask<GrainDirectoryPartitionSnapshot?> GetPartitionSnapshotAsync(MembershipVersion version, MembershipVersion rangeVersion, RingRange range);
    ValueTask<bool> AcknowledgeSnapshotTransferAsync(SiloAddress owner, MembershipVersion version);
}
