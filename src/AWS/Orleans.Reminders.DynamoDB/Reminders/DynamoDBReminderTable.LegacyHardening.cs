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

    private async Task<ReminderEntry?> ReadLegacyRow(GrainId grainId, string reminderName)
    {
        var item = await storage.ReadSingleEntryAsync(options.TableName, GetLegacyKey(grainId, reminderName), static item => item);
        if (item is null)
        {
            return null;
        }

        if (item[SERVICE_ID_PROPERTY_NAME].S != serviceId
            || item[GRAIN_REFERENCE_PROPERTY_NAME].S != grainId.ToString()
            || item[REMINDER_NAME_PROPERTY_NAME].S != reminderName)
        {
            throw new InvalidOperationException(
                $"DynamoDB legacy reminder '{reminderName}' for grain '{grainId}' in '{options.TableName}' has a conflicting stored identity.");
        }

        return Resolve(item);
    }
}
