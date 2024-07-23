using System.Collections.Generic;

#nullable enable
namespace Orleans.Runtime.GrainDirectory;

[GenerateSerializer, Alias(nameof(GrainDirectoryPartitionSnapshot)), Immutable]
internal sealed class GrainDirectoryPartitionSnapshot(
    MembershipVersion directoryMembershipVersion,
    List<GrainAddress> grainAddresses)
{
    [Id(0)]
    public MembershipVersion DirectoryMembershipVersion { get; } = directoryMembershipVersion;

    [Id(1)]
    public List<GrainAddress> GrainAddresses { get; } = grainAddresses;
}
