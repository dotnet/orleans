using System.Runtime.CompilerServices;
using Azure.Data.Tables;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Runtime;

namespace Orleans.Journaling;

internal sealed class AzureTableJournalStorageProvider : ILifecycleParticipant<ISiloLifecycle>, IJournalStorageProvider, IJournalStorageCatalog
{
    private static readonly string[] PartitionKeySelect = [nameof(ITableEntity.PartitionKey)];

    private readonly AzureTableJournalStorageOptions _options;
    private readonly AzureTableJournalStorage.InitializedTableClientProvider _tableClientProvider = new();
    private readonly AzureTableJournalStorage.AzureTableJournalStorageShared _shared;

    public AzureTableJournalStorageProvider(
        IOptions<AzureTableJournalStorageOptions> options,
        IOptions<JournaledStateManagerOptions> managerOptions,
        IServiceProvider serviceProvider,
        ILogger<AzureTableJournalStorage> logger,
        AzureTableJournalStorageInstruments? instruments = null)
    {
        _options = options.Value;
        var journalFormatKey = ValidateJournalFormatKey(managerOptions.Value.JournalFormatKey);
        ValidateJournalFormat(serviceProvider, journalFormatKey);
        _shared = new AzureTableJournalStorage.AzureTableJournalStorageShared(
            logger,
            options,
            _tableClientProvider,
            instruments ?? AzureTableJournalStorageInstruments.CreateForDirectConstruction(),
            journalFormatKey);
    }

    private async Task Initialize(CancellationToken cancellationToken)
    {
        var client = await _options.CreateClient!(cancellationToken);
        var table = client.GetTableClient(_options.TableName);
        await table.CreateIfNotExistsAsync(cancellationToken).ConfigureAwait(false);
        _tableClientProvider.SetTableClient(table);
    }

    public IJournalStorage CreateStorage(JournalId journalId)
    {
        if (journalId.IsDefault)
        {
            throw new ArgumentException("The journal id must not be the default value.", nameof(journalId));
        }

        return new AzureTableJournalStorage(_shared, journalId);
    }

    public async IAsyncEnumerable<JournalId> ListAsync(
        JournalId prefix = default,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var table = _tableClientProvider.GetTableClient();
        var filter = prefix.IsDefault
            ? TableClient.CreateQueryFilter($"RowKey eq {AzureTableJournalStorage.HeaderRowKey}")
            : TableClient.CreateQueryFilter(
                $"RowKey eq {AzureTableJournalStorage.HeaderRowKey} and PartitionKey ge {AzureTableJournalStorageOptions.GetDefaultPartitionKey(prefix)}");
        var journalIds = new List<JournalId>();
        await foreach (var entity in table.QueryAsync<TableEntity>(filter, select: PartitionKeySelect, cancellationToken: cancellationToken))
        {
            var storageIdValue = Uri.UnescapeDataString(entity.PartitionKey);
            if (TryParseJournalId(storageIdValue, out var journalId) && prefix.IsPrefixOf(journalId))
            {
                journalIds.Add(journalId);
            }
        }

        foreach (var journalId in journalIds.OrderBy(static journalId => journalId.Value, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return journalId;
        }
    }

    public void Participate(ISiloLifecycle observer)
    {
        observer.Subscribe(
            nameof(AzureTableJournalStorageProvider),
            ServiceLifecycleStage.RuntimeInitialize,
            onStart: Initialize);
    }

    private static bool TryParseJournalId(string value, out JournalId journalId)
    {
        try
        {
            journalId = new JournalId(value);
            return true;
        }
        catch (ArgumentException)
        {
            journalId = default;
            return false;
        }
    }

    private static void ValidateJournalFormat(IServiceProvider serviceProvider, string journalFormatKey)
    {
        var journalFormat = serviceProvider.GetKeyedService<IJournalFormat>(journalFormatKey);
        if (journalFormat is null)
        {
            throw new InvalidOperationException(
                $"Journal format key '{journalFormatKey}' requires keyed service '{typeof(IJournalFormat).FullName}', but none was registered.");
        }

        if (!string.Equals(journalFormat.FormatKey, journalFormatKey, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Journal format key '{journalFormatKey}' resolved format '{journalFormat.GetType().FullName}', but its {nameof(IJournalFormat.FormatKey)} is '{journalFormat.FormatKey}'. " +
                "Register the journal format using the same key it reports.");
        }
    }

    private static string ValidateJournalFormatKey(string? journalFormatKey)
    {
        if (string.IsNullOrWhiteSpace(journalFormatKey))
        {
            throw new InvalidOperationException("The configured journal format key must be non-empty.");
        }

        return journalFormatKey;
    }
}
