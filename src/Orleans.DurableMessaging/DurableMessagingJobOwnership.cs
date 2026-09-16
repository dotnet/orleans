using System;
using System.Collections.Generic;
using System.Globalization;
using Orleans.DurableJobs;
using Orleans.Journaling;
using Orleans.Runtime;

namespace Orleans.DurableMessaging;

internal static class DurableMessagingJobOwnership
{
    private const string MetadataKey = "orleans.messaging.ownership-id";

    public static IReadOnlyDictionary<string, string> CreateMetadata(string ownershipId) =>
        new Dictionary<string, string>(1, StringComparer.Ordinal)
        {
            [MetadataKey] = ownershipId
        };

    public static bool TryGetOwnershipId(DurableJob job, out string ownershipId)
    {
        if (job.Metadata is not null
            && job.Metadata.TryGetValue(MetadataKey, out var value)
            && !string.IsNullOrWhiteSpace(value))
        {
            ownershipId = value;
            return true;
        }

        ownershipId = string.Empty;
        return false;
    }


    public static bool HasOwner(string? ownershipId, DurableJob? job) =>
        !string.IsNullOrWhiteSpace(ownershipId) && job is not null;

    public static string? GetPairError(string? ownershipId, DurableJob? job)
    {
        var hasOwnershipId = !string.IsNullOrWhiteSpace(ownershipId);
        if (hasOwnershipId != (job is not null))
        {
            return "The durable messaging ownership generation and job handle must either both be present or both be absent.";
        }

        if (hasOwnershipId
            && (!TryGetOwnershipId(job!, out var jobOwnershipId)
                || !string.Equals(jobOwnershipId, ownershipId, StringComparison.Ordinal)))
        {
            return "The durable messaging job handle metadata does not match its ownership generation.";
        }

        return null;
    }

    public static bool IsSamePhysicalJob(DurableJob? expected, DurableJob? actual) =>
        expected is not null
        && actual is not null
        && string.Equals(expected.Id, actual.Id, StringComparison.Ordinal)
        && string.Equals(expected.ShardId, actual.ShardId, StringComparison.Ordinal);

    public static string NextId(string epoch, IDurableValue<long> sequence)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(epoch);
        sequence.Value++;
        return $"{epoch}:{sequence.Value.ToString(CultureInfo.InvariantCulture)}";
    }

    public static bool IsCompleted(string? completedOwnershipId, string ownershipId)
    {
        if (string.Equals(completedOwnershipId, ownershipId, StringComparison.Ordinal))
        {
            return true;
        }

        return TryParse(completedOwnershipId, out var completedEpoch, out var completed)
            && TryParse(ownershipId, out var currentEpoch, out var current)
            && string.Equals(completedEpoch, currentEpoch, StringComparison.Ordinal)
            && current <= completed;
    }

    private static bool TryParse(string? value, out string epoch, out long sequence)
    {
        sequence = 0;
        var separator = value?.LastIndexOf(':') ?? -1;
        if (separator <= 0
            || !long.TryParse(
                value.AsSpan(separator + 1),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out sequence))
        {
            epoch = string.Empty;
            return false;
        }

        epoch = value![..separator];
        return true;
    }

    public static OwnershipMismatchDisposition ResolveMismatch(
        bool recoveryCompleted,
        bool hasCurrentOwner,
        bool ownershipCompleted,
        bool hasWork)
    {
        if (!recoveryCompleted)
        {
            return OwnershipMismatchDisposition.WaitForRecovery;
        }

        if (hasCurrentOwner || ownershipCompleted)
        {
            return OwnershipMismatchDisposition.CompleteStale;
        }

        return hasWork
            ? OwnershipMismatchDisposition.WaitForReplacement
            : OwnershipMismatchDisposition.ReclaimOrphan;
    }
}

internal enum OwnershipMismatchDisposition
{
    WaitForRecovery,
    WaitForReplacement,
    CompleteStale,
    ReclaimOrphan
}
