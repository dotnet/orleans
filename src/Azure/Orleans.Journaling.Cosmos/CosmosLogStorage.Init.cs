using System.Net;

namespace Orleans.Journaling;

internal partial class CosmosLogStorage
{
    private async ValueTask EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
        {
            return;
        }

        await InitializeCosmosClient().ConfigureAwait(false);

        if (options.IsResourceCreationEnabled)
        {
            if (options.CleanResourcesOnInitialization)
            {
                await TryDeleteDatabase(cancellationToken).ConfigureAwait(false);
            }

            await TryCreateResources(cancellationToken).ConfigureAwait(false);
        }

        _container = _client.GetContainer(options.DatabaseName, options.ContainerName);

        // Check for a pending compaction first.
        try
        {
            var pendingResponse = await _container.ReadItemAsync<CosmosLogEntry>(
                    PendingCompactionEntryId, _partitionKey, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (pendingResponse.StatusCode == HttpStatusCode.OK && pendingResponse.Resource is { } pendingResource)
            {
                LogPendingCompactionFound(logger, logId);

                // A pending compaction exists. Attempt to complete it.
                await FinalizeCompactionAsync(pendingResource.Data, pendingResponse.ETag,
                    cancellationToken).ConfigureAwait(false);

                // State is now compacted!

                _initialized = true;
                _isCompacted = true;
                _logEntriesCount = 1;
                _nextSequenceNumber = 0;

                _compactedEntryETag = (await _container.ReadItemAsync<CosmosLogEntry>(
                        CompactedEntryId, _partitionKey, cancellationToken: cancellationToken)
                    .ConfigureAwait(false)).ETag;

                LogInitialized(logger, logId, _isCompacted, _logEntriesCount);

                return;
            }
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            // No pending compaction, proceed.
        }

        // Check for an existing compacted log.
        try
        {
            var compactedResponse = await _container.ReadItemAsync<CosmosLogEntry>(
                    CompactedEntryId, _partitionKey, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (compactedResponse.StatusCode == HttpStatusCode.OK)
            {
                _initialized = true;
                _isCompacted = true;
                _logEntriesCount = 1;
                _nextSequenceNumber = 0;
                _compactedEntryETag = compactedResponse.ETag;

                LogInitialized(logger, logId, _isCompacted, _logEntriesCount);

                return;
            }
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            // No compacted log, proceed to check for entries.
        }

        // Read existing log entries to determine current state
        List<CosmosLogEntry> entries = [];

        var query = new QueryDefinition(@"
                SELECT * FROM c
                WHERE c.LogId = @logId AND c.EntryType = @entryType
                ORDER BY c.SequenceNumber ASC")
            .WithParameter("@logId", logId)
            .WithParameter("@entryType", CosmosLogEntryType.Default);

        using var feed = _container.GetItemQueryIterator<CosmosLogEntry>(query, requestOptions: _requestOptions);
        long maxSequence = -1;

        while (feed.HasMoreResults)
        {
            foreach (var item in await feed.ReadNextAsync(cancellationToken).ConfigureAwait(false))
            {
                entries.Add(item);

                if (item.SequenceNumber > maxSequence)
                {
                    maxSequence = item.SequenceNumber;
                }
            }
        }

        _initialized = true;
        _isCompacted = false;
        _logEntriesCount = entries.Count;
        _nextSequenceNumber = entries.Count > 0 ? maxSequence + 1 : 0;
        _compactedEntryETag = null;

        LogInitialized(logger, logId, _isCompacted, _logEntriesCount);
    }

    private async Task InitializeCosmosClient()
    {
        try
        {
            _client = await options.CreateClient(serviceProvider).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogErrorInitializingClient(ex);
            WrappedException.CreateAndRethrow(ex);

            throw;
        }
    }

    private async Task TryDeleteDatabase(CancellationToken cancellationToken)
    {
        try
        {
            await _client.GetDatabase(options.DatabaseName)
                .DeleteAsync(cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return;
        }
        catch (Exception ex)
        {
            LogErrorDeletingDatabase(ex);
            WrappedException.CreateAndRethrow(ex);

            throw;
        }
    }

    private async Task TryCreateResources(CancellationToken cancellationToken)
    {
        var dbResponse = await _client.CreateDatabaseIfNotExistsAsync(
                options.DatabaseName, options.DatabaseThroughput, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        var db = dbResponse.Database;

        var logEntryProps = new ContainerProperties(options.ContainerName, "/LogId");

        logEntryProps.IndexingPolicy.IndexingMode = IndexingMode.Consistent;
        logEntryProps.IndexingPolicy.IncludedPaths.Add(new IncludedPath { Path = "/*" });
        logEntryProps.IndexingPolicy.ExcludedPaths.Add(new ExcludedPath { Path = "/Data/?" });

        const int maxRetries = 3;

        for (var retry = 0; retry <= maxRetries; ++retry)
        {
            var containerResponse = await db.CreateContainerIfNotExistsAsync(
                logEntryProps, options.ContainerThroughputProperties,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            if (containerResponse.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created)
            {
                break;
            }

            if (retry == maxRetries ||
                // If DB was not new, and container creation said OK but was not created
                dbResponse.StatusCode != HttpStatusCode.Created && containerResponse.StatusCode == HttpStatusCode.OK ||
                // If both DB and Container were newly created
                dbResponse.StatusCode == HttpStatusCode.Created && containerResponse.StatusCode == HttpStatusCode.Created)
            {
                break;
            }

            await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
        }
    }
}
