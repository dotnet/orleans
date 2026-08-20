using System.Collections.Generic;
using System.Globalization;
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

    public static string NextId(IDurableValue<long> sequence)
    {
        sequence.Value++;
        return sequence.Value.ToString(CultureInfo.InvariantCulture);
    }

    public static bool IsCompleted(string? completedOwnershipId, string ownershipId)
    {
        if (string.Equals(completedOwnershipId, ownershipId, StringComparison.Ordinal))
        {
            return true;
        }

        return long.TryParse(
                completedOwnershipId,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var completed)
            && long.TryParse(
                ownershipId,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var current)
            && current <= completed;
    }
}
