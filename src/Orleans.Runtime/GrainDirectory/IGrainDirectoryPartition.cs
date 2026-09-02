using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Concurrency;

namespace Orleans.Runtime.GrainDirectory;

[Alias("IGrainDirectoryPartition")]
internal interface IGrainDirectoryPartition : ISystemTarget
{
    [Alias("RegisterAsync")]
    ValueTask<DirectoryResult<GrainAddress>> RegisterAsync(
        MembershipVersion version,
        GrainAddress address,
        GrainAddress? currentRegistration,
        CancellationToken cancellationToken = default);

    [Alias("LookupAsync")]
    ValueTask<DirectoryResult<GrainAddress?>> LookupAsync(
        MembershipVersion version,
        GrainId grainId,
        CancellationToken cancellationToken = default);

    [Alias("DeregisterAsync")]
    ValueTask<DirectoryResult<bool>> DeregisterAsync(
        MembershipVersion version,
        GrainAddress address,
        CancellationToken cancellationToken = default);

    [Alias("GetSnapshotAsync")]
    ValueTask<GrainDirectoryPartitionSnapshot?> GetSnapshotAsync(
        MembershipVersion version,
        MembershipVersion rangeVersion,
        RingRange range,
        CancellationToken cancellationToken = default);

    [Alias("AcknowledgeSnapshotTransferAsync")]
    ValueTask<bool> AcknowledgeSnapshotTransferAsync(
        SiloAddress silo,
        int partitionIndex,
        MembershipVersion version,
        CancellationToken cancellationToken = default);
}

[Alias("IGrainDirectoryClient")]
internal interface IGrainDirectoryClient : ISystemTarget
{
    [Alias("GetRegisteredActivations")]
    ValueTask<Immutable<List<GrainAddress>>> GetRegisteredActivations(
        MembershipVersion membershipVersion,
        RingRange range,
        bool isValidation,
        CancellationToken cancellationToken = default);

    [Alias("RecoverRegisteredActivations")]
    ValueTask<Immutable<List<GrainAddress>>> RecoverRegisteredActivations(
        MembershipVersion membershipVersion,
        RingRange range,
        SiloAddress siloAddress,
        int partitionId,
        CancellationToken cancellationToken = default);
}

[Alias("IGrainDirectoryTestHooks")]
internal interface IGrainDirectoryTestHooks : ISystemTarget
{
    [Alias("CheckIntegrityAsync")]
    ValueTask CheckIntegrityAsync();

    [Alias("RecoverAndCheckIntegrityAsync")]
    ValueTask RecoverAndCheckIntegrityAsync();

    [Alias("WaitForMembershipVersionAsync")]
    ValueTask WaitForMembershipVersionAsync(MembershipVersion version);

    [Alias("CheckActivationsAsync")]
    ValueTask<Immutable<List<GrainId>>> CheckActivationsAsync(Immutable<List<GrainAddress>> activations);

    [Alias("CleanupExpiredLeasesAsync")]
    ValueTask<GrainDirectoryLeaseCleanupResult> CleanupExpiredLeasesAsync();
}

[GenerateSerializer, Immutable, Alias("GrainDirectoryLeaseCleanupResult")]
internal sealed class GrainDirectoryLeaseCleanupResult(
    int removedRangeLeaseHoldCount,
    int remainingRangeLeaseHoldCount,
    int removedSiloLeaseHoldCount,
    int remainingSiloLeaseHoldCount,
    int removedRegistrationCount,
    int remainingRegistrationCount)
{
    [Id(0)]
    public int RemovedRangeLeaseHoldCount { get; } = removedRangeLeaseHoldCount;

    [Id(1)]
    public int RemainingRangeLeaseHoldCount { get; } = remainingRangeLeaseHoldCount;

    [Id(2)]
    public int RemovedSiloLeaseHoldCount { get; } = removedSiloLeaseHoldCount;

    [Id(3)]
    public int RemainingSiloLeaseHoldCount { get; } = remainingSiloLeaseHoldCount;

    [Id(4)]
    public int RemovedRegistrationCount { get; } = removedRegistrationCount;

    [Id(5)]
    public int RemainingRegistrationCount { get; } = remainingRegistrationCount;
}
