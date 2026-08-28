using Amazon.DynamoDBv2.Model;
using Orleans.Runtime;

namespace Orleans.Reminders.DynamoDB;

internal sealed partial class DynamoDBReminderTable
{
    private const int LegacyPointReadConcurrency = 16;
    private static readonly TimeSpan LegacyStrongScanMinimumInterval = TimeSpan.FromSeconds(5);

    private async Task<List<ReminderEntry>> ConfirmLegacyDiscoveryCandidates(List<ReminderEntry> discovered)
    {
        IReadOnlyList<ReminderEntry> candidates = testHooks?.LegacyDiscoveryResults?.Invoke(discovered) ?? discovered;
        var result = new List<ReminderEntry>(candidates.Count);
        foreach (var batch in candidates.BatchIEnumerable(LegacyPointReadConcurrency))
        {
            var reads = batch.Select(entry => ReadLegacyRow(entry.GrainId, entry.ReminderName));
            foreach (var entry in await Task.WhenAll(reads))
            {
                if (entry is not null)
                {
                    result.Add(entry);
                }
            }
        }

        return result;
    }

    private Task<ReminderEntry?> ReadLegacyRow(GrainId grainId, string reminderName)
        => storage.ReadSingleEntryAsync(options.TableName, GetLegacyKey(grainId, reminderName), Resolve);

    private async Task<ReminderTableData> ReadLegacyRowsStrongly(uint begin, uint end)
        => await ReadLegacyRowsStrongly([(begin, end)]);

    private async Task<ReminderTableData> ReadLegacyRowsStrongly(IReadOnlyList<(uint Begin, uint End)> ranges)
    {
        await legacyStrongScanRateLimiter.WaitAsync();
        try
        {
            var delay = lastLegacyStrongScanStarted + LegacyStrongScanMinimumInterval - timeProvider.GetUtcNow();
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, timeProvider);
            }

            lastLegacyStrongScanStarted = timeProvider.GetUtcNow();
            var rows = await storage.ScanAsync(
                options.TableName,
                new Dictionary<string, AttributeValue> { [":service"] = new(serviceId) },
                $"{SERVICE_ID_PROPERTY_NAME} = :service",
                Resolve);
            return new(rows.Where(entry =>
                ranges.Any(range => IsInRange(entry.GrainId.GetUniformHashCode(), range.Begin, range.End))));
        }
        finally
        {
            legacyStrongScanRateLimiter.Release();
        }
    }

    private static bool IsInRange(uint hash, uint begin, uint end)
        => begin < end
            ? hash > begin && hash <= end
            : hash > begin || hash <= end;
}
