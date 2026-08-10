using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Runtime;
using Orleans.Configuration;


namespace Orleans.Reminders.Firestore;

internal partial class FirestoreReminderTable : IReminderTable
{
    private const string PERSISTENCE_GROUP = "Reminders";
    private readonly ILogger _logger;
    private readonly ClusterOptions _clusterOptions;
    private readonly FirestoreOptions _firestoreOptions;
    private readonly FirestoreDataManager _dataManager;
    private readonly TaskCompletionSource _initializationTask = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public FirestoreReminderTable(
        ILoggerFactory loggerFactory,
        IOptions<ClusterOptions> clusterOptions,
        IOptions<FirestoreOptions> firestoreOptions)
    {
        this._logger = loggerFactory.CreateLogger<FirestoreReminderTable>();
        this._clusterOptions = clusterOptions.Value;
        this._firestoreOptions = firestoreOptions.Value;
        this._dataManager = new FirestoreDataManager(
            PERSISTENCE_GROUP,
            Utils.SanitizeId(this._clusterOptions.ServiceId),
            this._firestoreOptions,
            loggerFactory.CreateLogger<FirestoreDataManager>());
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                var sw = Stopwatch.StartNew();
                try
                {
                    LogInitializing();
                    await this._dataManager.Initialize(cancellationToken);
                    this._initializationTask.TrySetResult();
                    LogInitialized(sw.ElapsedMilliseconds);
                    return;
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                {
                    LogInitializationRetry(ex, sw.ElapsedMilliseconds);
                    await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
                }
                finally
                {
                    sw.Stop();
                }
            }
        }
        catch (OperationCanceledException ex)
        {
            this._initializationTask.TrySetCanceled(ex.CancellationToken);
            throw;
        }
        catch (Exception ex)
        {
            this._initializationTask.TrySetException(ex);
            throw;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        this._initializationTask.TrySetCanceled(CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task<string?> UpsertRow(ReminderEntry entry)
    {
        try
        {
            await this._initializationTask.Task;

            LogUpsertRow(entry);

            var entity = new ReminderEntity
            {
                StartAt = DateTime.SpecifyKind(entry.StartAt, DateTimeKind.Utc),
                Period = entry.Period.Ticks,
                GrainHash = entry.GrainId.GetUniformHashCode(),
                Name = entry.ReminderName,
                Id = FormatReminderId(entry),
                GrainId = entry.GrainId.ToString()
            };

            if (string.IsNullOrWhiteSpace(entry.ETag) || entry.ETag == "*")
            {
                return await this._dataManager.UpsertEntity(entity).ConfigureAwait(false);
            }

            entity.ETag = Utils.ParseTimestamp(entry.ETag);
            return await this._dataManager.Update(entity).ConfigureAwait(false);
        }
        catch (RpcException ex) when (IsContention(ex))
        {
            return null;
        }
        catch (FormatException)
        {
            return null;
        }
        catch (Exception ex)
        {
            LogUpsertError(ex, entry);
            throw;
        }
    }

    public async Task<bool> RemoveRow(GrainId grainId, string reminderName, string eTag)
    {
        try
        {
            await this._initializationTask.Task;

            LogRemoveRow(grainId, reminderName);

            var result = await this._dataManager.DeleteEntity(FormatReminderId(reminderName, grainId), eTag).ConfigureAwait(false);

            return result;
        }
        catch (FormatException)
        {
            return false;
        }
        catch (Exception exc)
        {
            LogRemoveError(exc, grainId, reminderName);
            throw;
        }
    }

    public async Task<ReminderTableData> ReadRows(GrainId grainId)
    {
        try
        {
            await this._initializationTask.Task;

            var entries = await this._dataManager.QueryEntities<ReminderEntity>(
                reminder => reminder
                    .WhereEqualTo(nameof(ReminderEntity.GrainId), grainId.ToString())
                ).ConfigureAwait(false);

            var data = ConvertFromEntities(entries);

            LogReadForGrain(grainId, data);

            return data;
        }
        catch (Exception exc)
        {
            LogReadForGrainError(exc, grainId);
            throw;
        }
    }

    public async Task<ReminderTableData> ReadRows(uint begin, uint end)
    {
        try
        {
            await this._initializationTask.Task;

            var entries = new List<ReminderEntity>();

            if (begin < end)
            {
                var results = await this._dataManager.QueryEntities<ReminderEntity>(
                    reminder => reminder
                        .WhereGreaterThan(nameof(ReminderEntity.GrainHash), begin)
                        .WhereLessThanOrEqualTo(nameof(ReminderEntity.GrainHash), end)
                    ).ConfigureAwait(false);

                entries.AddRange(results);
            }
            else
            {
                var collection = this._dataManager.GetCollection();
                var results = await this._dataManager.ExecuteTransaction(async transaction =>
                {
                    var lowerRange = await transaction.GetSnapshotAsync(
                        collection.WhereLessThanOrEqualTo(nameof(ReminderEntity.GrainHash), end),
                        transaction.CancellationToken);
                    var upperRange = await transaction.GetSnapshotAsync(
                        collection.WhereGreaterThan(nameof(ReminderEntity.GrainHash), begin),
                        transaction.CancellationToken);
                    return lowerRange.Documents
                        .Concat(upperRange.Documents)
                        .Select(document => document.ConvertTo<ReminderEntity>())
                        .ToArray();
                }).ConfigureAwait(false);

                entries.AddRange(results);
            }

            var data = ConvertFromEntities(entries);

            LogReadForRange(RangeFactory.CreateRange(begin, end), data);

            return data;
        }
        catch (Exception exc)
        {
            LogReadForRangeError(exc, RangeFactory.CreateRange(begin, end));
            throw;
        }
    }

    public async Task<ReminderEntry?> ReadRow(GrainId grainId, string reminderName)
    {
        try
        {
            await this._initializationTask.Task;

            var entity = await this._dataManager.ReadEntity<ReminderEntity>(FormatReminderId(reminderName, grainId)).ConfigureAwait(false);

            if (entity is null) return null;

            var entry = ConvertFromEntity(entity);

            LogReadRow(grainId, entry);

            return entry;
        }
        catch (Exception exc)
        {
            LogReadRowError(exc, grainId, reminderName);
            throw;
        }
    }

    public async Task TestOnlyClearTable()
    {
        await this._initializationTask.Task;

        var entities = await this._dataManager.ReadAllEntities<ReminderEntity>().ConfigureAwait(false);

        var tasks = new List<Task>();
        foreach (var entity in entities)
        {
            tasks.Add(this._dataManager.DeleteEntity(entity.Id, Utils.FormatTimestamp(entity.ETag!.Value)));
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);

        LogClearedTable();
    }

    private static string FormatReminderId(ReminderEntry entry) => FormatReminderId(entry.ReminderName, entry.GrainId);
    private static string FormatReminderId(string reminderName, GrainId grainId) =>
        $"{Utils.SanitizeId(reminderName)}.{Utils.SanitizeGrainId(grainId)}";

    private static bool IsContention(RpcException exception) =>
        exception.StatusCode is StatusCode.Aborted or StatusCode.AlreadyExists or StatusCode.FailedPrecondition or StatusCode.NotFound;

    private ReminderTableData ConvertFromEntities(IEnumerable<ReminderEntity> entities)
    {
        var data = new List<ReminderEntry>();

        foreach (var entity in entities)
        {
            try
            {
                data.Add(ConvertFromEntity(entity));
            }
            catch (Exception exc)
            {
                LogParseError(exc, entity.GrainId, entity.Name);
            }
        }

        return new ReminderTableData(data);
    }

    private ReminderEntry ConvertFromEntity(ReminderEntity entity)
    {
        try
        {
            return new ReminderEntry
            {
                GrainId = GrainId.Parse(entity.GrainId),
                ReminderName = entity.Name,
                StartAt = entity.StartAt.UtcDateTime,
                Period = TimeSpan.FromTicks(entity.Period),
                ETag = Utils.FormatTimestamp(entity.ETag!.Value),
            };
        }
        catch (Exception exc)
        {
            LogParseError(exc, entity.GrainId, entity.Name);
            throw;
        }
    }

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Initializing Firestore reminders table...")]
    private partial void LogInitializing();

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Initialized Firestore reminders table in {ElapsedMilliseconds}ms.")]
    private partial void LogInitialized(long elapsedMilliseconds);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Error initializing Firestore reminders table in {ElapsedMilliseconds}ms. Retrying.")]
    private partial void LogInitializationRetry(Exception exception, long elapsedMilliseconds);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "UpsertRow entry = {Data}")]
    private partial void LogUpsertRow(ReminderEntry data);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Intermediate error upserting reminder entry {Data} to Firestore.")]
    private partial void LogUpsertError(Exception exception, ReminderEntry data);

    [LoggerMessage(
        Level = LogLevel.Trace,
        Message = "RemoveRow entry = {GrainId} name = {Name}")]
    private partial void LogRemoveRow(GrainId grainId, string name);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Intermediate error when deleting reminder entry = {GrainId} name = {Name} on Firestore.")]
    private partial void LogRemoveError(Exception exception, GrainId grainId, string name);

    [LoggerMessage(
        Level = LogLevel.Trace,
        Message = "Read for grain {GrainId} Table={Data}")]
    private partial void LogReadForGrain(GrainId grainId, ReminderTableData data);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Intermediate error reading reminders for grain {GrainId} from Firestore.")]
    private partial void LogReadForGrainError(Exception exception, GrainId grainId);

    [LoggerMessage(
        Level = LogLevel.Trace,
        Message = "Read for range {RingRange} Table={Data}")]
    private partial void LogReadForRange(object ringRange, ReminderTableData data);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Intermediate error reading reminders in range {RingRange} from Firestore.")]
    private partial void LogReadForRangeError(Exception exception, object ringRange);

    [LoggerMessage(
        Level = LogLevel.Trace,
        Message = "Read for grain {GrainId} Table={Data}")]
    private partial void LogReadRow(GrainId grainId, ReminderEntry data);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Intermediate error reading reminder entry = {GrainId} name = {Name} from Firestore.")]
    private partial void LogReadRowError(Exception exception, GrainId grainId, string name);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "TestOnlyClearTable completed successfully.")]
    private partial void LogClearedTable();

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Failed to parse ReminderTableEntry entry = {GrainId} name = {Name}. This entry is corrupt, going to ignore it.")]
    private partial void LogParseError(Exception exception, string grainId, string name);
}
