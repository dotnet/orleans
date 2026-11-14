using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using Orleans.Configuration;
using Orleans.Reminders.MongoDB;
using Orleans.Providers.MongoDB.Reminders.Store;
using Orleans.Runtime;


namespace Orleans.Providers.MongoDB.Reminders;

/// <summary>
/// Stores durable Orleans reminders in MongoDB, partitioned by service identity.
/// </summary>
public sealed class MongoReminderTable : IReminderTable
{
    private readonly IMongoClient mongoClient;
    private readonly ILogger logger;
    private readonly MongoDBRemindersOptions options;
    private readonly string serviceId;
    private MongoReminderCollection collection = null!;

    /// <summary>
    /// Initializes reminder storage using the configured MongoDB client and service identity.
    /// </summary>
    public MongoReminderTable(
        IMongoClient mongoClient,
        ILogger<MongoReminderTable> logger,
        IOptions<MongoDBRemindersOptions> options,
        IOptions<ClusterOptions> clusterOptions)
    {
        this.mongoClient = mongoClient;
        this.logger = logger;
        this.options = options.Value;
        this.serviceId = clusterOptions.Value.ServiceId;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        collection =
            new MongoReminderCollection(
                mongoClient,
                options.DatabaseName,
                options.CollectionPrefix,
                options.CollectionConfigurator,
                options.CreateShardKeyForCosmos,
                options.CreateIndexes,
#pragma warning disable CS0618 // Retained for rolling upgrades from the contributed provider.
                options.PurgeLegacyIndexes,
#pragma warning restore CS0618
                serviceId);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<ReminderTableData> ReadRows(GrainId grainId)
        => ReadRows(grainId, CancellationToken.None);

    /// <inheritdoc />
    public Task<ReminderTableData> ReadRows(GrainId grainId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return DoAndLog(nameof(ReadRows), () =>
        {
            return collection.ReadRow(grainId, cancellationToken);
        });
    }

    /// <inheritdoc />
    public Task<bool> RemoveRow(GrainId grainId, string reminderName, string eTag)
        => RemoveRow(grainId, reminderName, eTag, CancellationToken.None);

    /// <inheritdoc />
    public Task<bool> RemoveRow(GrainId grainId, string reminderName, string eTag, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CheckGrainId(grainId);
        ArgumentException.ThrowIfNullOrEmpty(reminderName);

        return DoAndLog(nameof(RemoveRow), () =>
        {
            return collection.RemoveRow(grainId, reminderName, eTag, cancellationToken);
        });
    }

    /// <inheritdoc />
    public Task<ReminderEntry?> ReadRow(GrainId grainId, string reminderName)
        => ReadRow(grainId, reminderName, CancellationToken.None);

    /// <inheritdoc />
    public Task<ReminderEntry?> ReadRow(GrainId grainId, string reminderName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CheckGrainId(grainId);
        ArgumentException.ThrowIfNullOrEmpty(reminderName);

        return DoAndLog(nameof(ReadRow), () =>
        {
            return collection.ReadRow(grainId, reminderName, cancellationToken);
        });
    }

    /// <inheritdoc />
    public Task TestOnlyClearTable()
        => TestOnlyClearTable(CancellationToken.None);

    /// <inheritdoc />
    public Task TestOnlyClearTable(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return DoAndLog(nameof(TestOnlyClearTable), () =>
        {
            return collection.RemoveRows(cancellationToken);
        });
    }

    /// <inheritdoc />
    public Task<string?> UpsertRow(ReminderEntry entry)
        => UpsertRow(entry, CancellationToken.None);

    /// <inheritdoc />
    public Task<string?> UpsertRow(ReminderEntry entry, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(entry);
        CheckGrainId(entry.GrainId);
        ArgumentException.ThrowIfNullOrEmpty(entry.ReminderName);

        return DoAndLog<string?>(nameof(UpsertRow), async () =>
        {
            return await collection.UpsertRow(entry, cancellationToken);
        });
    }

    /// <inheritdoc />
    public Task<ReminderTableData> ReadRows(uint begin, uint end)
        => ReadRows(begin, end, CancellationToken.None);

    /// <inheritdoc />
    public Task<ReminderTableData> ReadRows(uint begin, uint end, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return DoAndLog(nameof(ReadRows), () =>
        {
            return collection.ReadRows(begin, end, cancellationToken);
        });
    }

    private Task DoAndLog(string actionName, Func<Task> action)
    {
        return DoAndLog(actionName, async () => { await action(); return true; });
    }

    private async Task<T> DoAndLog<T>(string actionName, Func<Task<T>> action)
    {
        logger.LogDebug($"ReminderTable.{actionName} called.");

        try
        {
            return await action();
        }
        catch (Exception ex)
        {
            logger.LogError((int)MongoProviderErrorCode.Reminders_Operations, ex, $"ReminderTable.{actionName} failed. Exception={ex.Message}");

            throw;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void CheckGrainId(GrainId grainId)
    {
        if (grainId.IsDefault)
            throw new InvalidOperationException(
                "Grain identifier is default. Was the reminder registry called from a client application?");
    }
}
