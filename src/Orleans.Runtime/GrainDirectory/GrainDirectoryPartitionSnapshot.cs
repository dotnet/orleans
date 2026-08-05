using System.Collections.Generic;

namespace Orleans.Runtime.GrainDirectory;

[GenerateSerializer, Alias(nameof(GrainDirectoryPartitionSnapshot)), Immutable]
internal sealed class GrainDirectoryPartitionSnapshot(
    MembershipVersion directoryMembershipVersion,
    List<GrainAddress> grainAddresses,
    List<GrainDirectoryRangeLease>? rangeLeaseHolds = null)
{
    [Id(0)]
    public MembershipVersion DirectoryMembershipVersion { get; } = directoryMembershipVersion;

    [Id(1)]
    public List<GrainAddress> GrainAddresses { get; } = grainAddresses;

    [Id(2)]
    public List<GrainDirectoryRangeLease>? RangeLeaseHolds { get; } = rangeLeaseHolds;
}

[GenerateSerializer, Alias(nameof(GrainDirectoryRangeLease)), Immutable]
internal sealed class GrainDirectoryRangeLease(RingRange range, DateTimeOffset expiration)
{
    [Id(0)]
    public RingRange Range { get; } = range;

    [Id(1)]
    public DateTimeOffset Expiration { get; } = expiration;
}
