using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace Orleans.DurableJobs;

internal static class DurableJobIdentity
{
    public static string CreateId(in ScheduleJobRequest request)
    {
        if (request.IdempotencyKey is null)
        {
            return Guid.NewGuid().ToString();
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(request.IdempotencyKey);
        var value = string.Concat(request.Target.ToString(), "\n", request.IdempotencyKey);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }

    public static bool IsEquivalent(DurableJob job, in ScheduleJobRequest request)
        => job.TargetGrainId == request.Target
            && string.Equals(job.Name, request.JobName, StringComparison.Ordinal)
            && job.DueTime == request.DueTime
            && job.Priority == request.Priority
            && MetadataEquals(job.Metadata, request.Metadata);

    public static int GetStableHashCode(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return BinaryPrimitives.ReadInt32LittleEndian(hash) & int.MaxValue;
    }

    private static bool MetadataEquals(IReadOnlyDictionary<string, string>? left, IReadOnlyDictionary<string, string>? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left is null || right is null || left.Count != right.Count)
        {
            return false;
        }

        foreach (var (key, value) in left)
        {
            if (!right.TryGetValue(key, out var candidate) || !string.Equals(value, candidate, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}
