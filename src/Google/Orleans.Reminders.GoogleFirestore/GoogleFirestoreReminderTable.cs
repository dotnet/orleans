using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Runtime;
using Orleans.Configuration;


namespace Orleans.Reminders.GoogleFirestore;

internal class GoogleFirestoreReminderTable : IReminderTable
{
    private const string PERSISTENCE_GROUP = "Reminders";
    private readonly ILogger _logger;
    private readonly ClusterOptions _clusterOptions;
    private readonly FirestoreOptions _firestoreOptions;
    private readonly FirestoreDataManager _dataManager;

    public GoogleFirestoreReminderTable(
        ILoggerFactory loggerFactory,
        IOptions<ClusterOptions> clusterOptions,
        IOptions<FirestoreOptions> firestoreOptions)
    {
        this._logger = loggerFactory.CreateLogger<GoogleFirestoreReminderTable>();
        this._clusterOptions = clusterOptions.Value;
        this._firestoreOptions = firestoreOptions.Value;
        this._dataManager = new FirestoreDataManager(
            PERSISTENCE_GROUP,
            Utils.SanitizeId(this._clusterOptions.ServiceId),
            this._firestoreOptions,
            loggerFactory.CreateLogger<FirestoreDataManager>());
    }

    public async Task Init()
    {
        var sw = Stopwatch.StartNew();

        try
        {
            this._logger.LogInformation("Initializing GoogleFirestoreStorage Reminders table...");

            await this._dataManager.Initialize();

            this._logger.LogInformation("Initializing GoogleFirestoreStorage Reminders table took {ElapsedMilliseconds}ms.", sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            this._logger.LogError(ex, "Error initializing GoogleFirestoreStorage Reminders table in {ElapsedMilliseconds}.", sw.ElapsedMilliseconds);
            throw;
        }
        finally
        {
            sw.Stop();
        }
    }

    public async Task<string> UpsertRow(ReminderEntry entry)
    {
        try
        {
            if (this._logger.IsEnabled(LogLevel.Debug)) this._logger.LogDebug("UpsertRow entry = {Data}", entry.ToString());

            var entity = new ReminderEntity
            {
                StartAt = entry.StartAt.ToUniversalTime(),
                Period = entry.Period.Ticks,
                GrainHash = entry.GrainId.GetUniformHashCode(),
                Name = entry.ReminderName,
                Id = FormatReminderId(entry),
                GrainId = entry.GrainId.ToString()
            };

            if (!string.IsNullOrWhiteSpace(entry.ETag))
            {
                entity.ETag = Utils.ParseTimestamp(entry.ETag);
            }

            var newETag = await this._dataManager.UpsertEntity(entity).ConfigureAwait(false);

            return newETag;
        }
        // catch (RpcException ex)
        // {
        //     if (ex.StatusCode == StatusCode.AlreadyExists)
        //     {
        //         var existing = await this._dataManager.ReadEntity<ReminderEntity>(FormatReminderId(entry)).ConfigureAwait(false);
        //         return Utils.FormatTimestamp(existing!.ETag!.Value);
        //     }
        //     throw;
        // }
        catch (Exception ex)
        {
            this._logger.LogWarning(ex,
                "Intermediate error upserting reminder entry {Data} to Firestore.", entry.ToString());
            throw;
        }
    }

    public async Task<bool> RemoveRow(GrainId grainId, string reminderName, string eTag)
    {
        try
        {
            if (this._logger.IsEnabled(LogLevel.Trace)) this._logger.LogTrace("RemoveRow entry = {GrainId} name = {Name}", grainId, reminderName);

            var result = await this._dataManager.DeleteEntity(FormatReminderId(reminderName, grainId), eTag).ConfigureAwait(false);

            return result;
        }
        catch (Exception exc)
        {
            this._logger.LogWarning(exc,
                "Intermediate error when deleting reminder entry = {GrainId} name = {Name} on Firestore.", grainId, reminderName);
            throw;
        }
    }

    public async Task<ReminderTableData> ReadRows(GrainId grainId)
    {
        try
        {
            var entries = await this._dataManager.QueryEntities<ReminderEntity>(
                reminder => reminder
                    .WhereEqualTo(nameof(ReminderEntity.GrainId), grainId.ToString())
                ).ConfigureAwait(false);

            var data = ConvertFromEntities(entries);

            if (this._logger.IsEnabled(LogLevel.Trace)) this._logger.LogTrace("Read for grain {GrainId} Table={Data}", grainId, data.ToString());

            return data;
        }
        catch (Exception exc)
        {
            this._logger.LogWarning(exc,
                "Intermediate error reading reminders for grain {GrainId} from Firestore.", grainId);
            throw;
        }
    }

    public async Task<ReminderTableData> ReadRows(uint begin, uint end)
    {
        try
        {
            var entries = new List<ReminderEntity>();

            if (begin < end)
            {
                var results = await this._dataManager.QueryEntities<ReminderEntity>(
                    reminder => reminder
                        .WhereGreaterThanOrEqualTo(nameof(ReminderEntity.GrainHash), begin)
                        .WhereLessThanOrEqualTo(nameof(ReminderEntity.GrainHash), end)
                    ).ConfigureAwait(false);

                entries.AddRange(results);
            }
            else
            {
                var results = await this._dataManager.QueryEntities<ReminderEntity>(
                    reminder => reminder
                        .WhereLessThanOrEqualTo(nameof(ReminderEntity.GrainHash), end)
                    ).ConfigureAwait(false);

                entries.AddRange(results);

                results = await this._dataManager.QueryEntities<ReminderEntity>(
                    reminder => reminder
                        .WhereGreaterThan(nameof(ReminderEntity.GrainHash), begin)
                    ).ConfigureAwait(false);

                entries.AddRange(results);
            }

            var data = ConvertFromEntities(entries);

            if (this._logger.IsEnabled(LogLevel.Trace)) this._logger.LogTrace("Read for grain {RingRange} Table={Data}", RangeFactory.CreateRange(begin, end), data.ToString());

            return data;
        }
        catch (Exception exc)
        {
            this._logger.LogWarning(exc,
                "Intermediate error reading reminders in range {RingRange} from Firestore.", RangeFactory.CreateRange(begin, end));
            throw;
        }
    }

    public async Task<ReminderEntry?> ReadRow(GrainId grainId, string reminderName)
    {
        try
        {
            var entity = await this._dataManager.ReadEntity<ReminderEntity>(FormatReminderId(reminderName, grainId)).ConfigureAwait(false);

            if (entity is null) return null;

            var entry = ConvertFromEntity(entity);

            if (this._logger.IsEnabled(LogLevel.Trace)) this._logger.LogTrace("Read for grain {GrainId} Table={Data}", grainId, entry.ToString());

            return entry;
        }
        catch (Exception exc)
        {
            this._logger.LogWarning(exc,
                "Intermediate error reading reminder entry = {GrainId} name = {Name} from Firestore.", grainId, reminderName);
            throw;
        }
    }

    public async Task TestOnlyClearTable()
    {
        var entities = await this._dataManager.ReadAllEntities<ReminderEntity>().ConfigureAwait(false);

        var tasks = new List<Task>();
        foreach (var entity in entities)
        {
            tasks.Add(this._dataManager.DeleteEntity(entity.Id, Utils.FormatTimestamp(entity.ETag!.Value)));
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);

        this._logger.LogInformation("TestOnlyClearTable completed successfully.");
    }

    private static string FormatReminderId(ReminderEntry entry) => FormatReminderId(entry.ReminderName, entry.GrainId);
    private static string FormatReminderId(string reminderName, GrainId grainId) => Utils.SanitizeId($"{reminderName}__{grainId}");

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
                this._logger.LogError(exc, "Failed to parse ReminderTableEntry entry = {GrainId} name = {Name}. This entry is corrupt, going to ignore it.",
                    entity.GrainId, entity.Name);
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
                GrainId = Utils.ParseGrainId(entity.GrainId),
                ReminderName = entity.Name,
                StartAt = entity.StartAt.UtcDateTime,
                Period = TimeSpan.FromTicks(entity.Period),
                ETag = Utils.FormatTimestamp(entity.ETag!.Value),
            };
        }
        catch (Exception exc)
        {
            this._logger.LogError(exc, "Failed to parse ReminderTableEntry entry = {GrainId} name = {Name}. This entry is corrupt, going to ignore it.",
                entity.GrainId, entity.Name);
            throw;
        }
    }
}
