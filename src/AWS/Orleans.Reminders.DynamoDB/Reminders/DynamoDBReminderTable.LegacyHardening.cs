using Orleans.Runtime;

namespace Orleans.Reminders.DynamoDB;

internal sealed partial class DynamoDBReminderTable
{
    private const int LegacyPointReadConcurrency = 16;

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

}
