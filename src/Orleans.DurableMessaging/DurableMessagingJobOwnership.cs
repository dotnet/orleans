using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Orleans.DurableJobs;
using Orleans.Journaling;

namespace Orleans.DurableMessaging;

internal static class DurableMessagingJobOwnership
{
    private const string MetadataKey = "orleans.messaging.ownership-id";

    public static IReadOnlyDictionary<string, string> CreateMetadata(string ownershipId) =>
        new Dictionary<string, string>(1, StringComparer.Ordinal)
        {
            [MetadataKey] = ownershipId
        };

    public static string CreateJobId(string jobName, GrainId target, string ownershipId) =>
        $"{Encode(Encoding.UTF8.GetBytes(jobName))}."
        + $"{Encode(target.Type.Value.Value.Span)}."
        + $"{Encode(target.Key.Value.Span)}."
        + $"{Encode(Encoding.UTF8.GetBytes(ownershipId))}";

    public static bool TryGetOwnershipId(DurableJob job, out string ownershipId)
    {
        if (job.Metadata is not null
            && job.Metadata.TryGetValue(MetadataKey, out var value)
            && !string.IsNullOrEmpty(value))
        {
            ownershipId = value;
            return true;
        }

        ownershipId = job.Id;
        return false;
    }

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

    private static string Encode(ReadOnlySpan<byte> value) => Convert.ToHexString(value);

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
