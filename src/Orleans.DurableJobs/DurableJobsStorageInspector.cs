using Microsoft.Extensions.Logging;
using Orleans.Journaling;

namespace Orleans.DurableJobs;

internal sealed partial class DurableJobsStorageInspector(
    DurableJobsJournalProviders providers,
    ILogger<DurableJobsStorageInspector> logger) : IDurableJobsStorageInspector
{
    public async ValueTask<DurableJobsStorageStatus> InspectAsync(string providerName, CancellationToken cancellationToken = default)
    {
        var provider = providers.GetProvider(providerName);
        try
        {
            long count = 0, owned = 0, poisoned = 0, unrecognized = 0;
            DateTimeOffset? oldest = null, newest = null;
            var seen = new HashSet<JournalId>();
            var options = new ListOptions
            {
                Prefix = new JournalId(JobShardId.StoragePrefix.Value + "/"),
                IncludeMetadata = true
            };
            await foreach (var entry in provider.Catalog.ListAsync(options, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!seen.Add(entry.Id))
                {
                    continue;
                }

                var metadata = entry.Metadata ?? await provider.Storage.CreateStorage(entry.Id).GetMetadataAsync(cancellationToken);
                if (metadata is null)
                {
                    continue;
                }

                count++;
                var descriptor = JournaledJobShardManager.ShardCatalogProperties.From(provider, entry.Id, metadata);
                if (descriptor is null)
                {
                    unrecognized++;
                    continue;
                }

                owned += descriptor.Owner is not null ? 1 : 0;
                poisoned += descriptor.Poisoned ? 1 : 0;
                if (oldest is null || descriptor.StartTime < oldest)
                {
                    oldest = descriptor.StartTime;
                }

                if (newest is null || descriptor.StartTime > newest)
                {
                    newest = descriptor.StartTime;
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            LogInventory(logger, providerName, count, owned, poisoned, unrecognized);
            return new(providerName, ReferenceEquals(provider, providers.WriteProvider), count, owned, poisoned, unrecognized, oldest, newest);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            LogInspectionFailure(logger, exception, providerName);
            throw;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Durable Jobs provider '{ProviderName}' inventory: {ShardCount} shards, {OwnedShardCount} owned, {PoisonedShardCount} poisoned, {UnrecognizedShardCount} unrecognized.")]
    private static partial void LogInventory(ILogger logger, string providerName, long shardCount, long ownedShardCount, long poisonedShardCount, long unrecognizedShardCount);

    [LoggerMessage(Level = LogLevel.Error, Message = "Durable Jobs provider '{ProviderName}' inventory failed.")]
    private static partial void LogInspectionFailure(ILogger logger, Exception exception, string providerName);
}
