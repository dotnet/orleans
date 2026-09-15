using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;

namespace Orleans.AdvancedReminders.DynamoDB;


/// <summary>
/// Implementation for IReminderTable using DynamoDB as underlying storage.
/// </summary>
internal sealed partial class DynamoDBReminderTable : IReminderTable
{
    private const string GRAIN_REFERENCE_PROPERTY_NAME = "GrainReference";
    private const string REMINDER_NAME_PROPERTY_NAME = "ReminderName";
    private const string SERVICE_ID_PROPERTY_NAME = "ServiceId";
    private const string START_TIME_PROPERTY_NAME = "StartTime";
    private const string PERIOD_PROPERTY_NAME = "Period";
    private const string CRON_EXPRESSION_PROPERTY_NAME = "CronExpression";
    private const string CRON_TIME_ZONE_ID_PROPERTY_NAME = "CronTimeZoneId";
    private const string NEXT_DUE_UTC_PROPERTY_NAME = "NextDueUtc";
    private const string LAST_FIRE_UTC_PROPERTY_NAME = "LastFireUtc";
    private const string SCHEDULE_ID_PROPERTY_NAME = "ScheduleId";
    private const string JOB_ID_PROPERTY_NAME = "JobId";
    private const string JOB_SHARD_ID_PROPERTY_NAME = "JobShardId";
    private const string ACTION_PROPERTY_NAME = "Action";
    private const string GRAIN_HASH_PROPERTY_NAME = "GrainHash";
    private const string REMINDER_ID_PROPERTY_NAME = "ReminderId";
    private const string ETAG_PROPERTY_NAME = "ETag";
    private const string CURRENT_ETAG_ALIAS = ":currentETag";
    private const string SERVICE_ID_GRAIN_HASH_INDEX = "ServiceIdIndex";
    private const string SERVICE_ID_GRAIN_REFERENCE_INDEX = "ServiceIdGrainReferenceIndex";

    private readonly ILogger logger;
    private readonly DynamoDBReminderStorageOptions options;
    private readonly string serviceId;

    private DynamoDBStorage storage = default!;

    /// <summary>Initializes a new instance of the <see cref="DynamoDBReminderTable"/> class.</summary>
    /// <param name="loggerFactory">logger factory to use</param>
    /// <param name="clusterOptions"></param>
    /// <param name="storageOptions"></param>
    public DynamoDBReminderTable(
        ILoggerFactory loggerFactory,
        IOptions<ClusterOptions> clusterOptions,
        IOptions<DynamoDBReminderStorageOptions> storageOptions)
    {
        this.logger = loggerFactory.CreateLogger<DynamoDBReminderTable>();
        this.serviceId = clusterOptions.Value.ServiceId;
        this.options = storageOptions.Value;
    }

    /// <summary>Initialize current instance with specific global configuration and logger</summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        this.storage = new DynamoDBStorage(
            this.logger,
            this.options.Service,
            this.options.AccessKey,
            this.options.SecretKey,
            this.options.Token,
            this.options.ProfileName,
            this.options.ReadCapacityUnits,
            this.options.WriteCapacityUnits,
            this.options.UseProvisionedThroughput,
            this.options.CreateIfNotExists,
            this.options.UpdateIfExists);

        LogInformationInitializingDynamoDBRemindersTable(logger);

        var serviceIdGrainHashGlobalSecondaryIndex = new GlobalSecondaryIndex
        {
            IndexName = SERVICE_ID_GRAIN_HASH_INDEX,
            Projection = new Projection { ProjectionType = ProjectionType.ALL },
            KeySchema = new List<KeySchemaElement>
            {
                new KeySchemaElement { AttributeName = SERVICE_ID_PROPERTY_NAME, KeyType = KeyType.HASH},
                new KeySchemaElement { AttributeName = GRAIN_HASH_PROPERTY_NAME, KeyType = KeyType.RANGE }
            }
        };

        var serviceIdGrainReferenceGlobalSecondaryIndex = new GlobalSecondaryIndex
        {
            IndexName = SERVICE_ID_GRAIN_REFERENCE_INDEX,
            Projection = new Projection { ProjectionType = ProjectionType.ALL },
            KeySchema = new List<KeySchemaElement>
            {
                new KeySchemaElement { AttributeName = SERVICE_ID_PROPERTY_NAME, KeyType = KeyType.HASH},
                new KeySchemaElement { AttributeName = GRAIN_REFERENCE_PROPERTY_NAME, KeyType = KeyType.RANGE }
            }
        };

        return this.storage.InitializeTable(this.options.TableName,
            new List<KeySchemaElement>
            {
                new KeySchemaElement { AttributeName = REMINDER_ID_PROPERTY_NAME, KeyType = KeyType.HASH },
                new KeySchemaElement { AttributeName = GRAIN_HASH_PROPERTY_NAME, KeyType = KeyType.RANGE }
            },
            new List<AttributeDefinition>
            {
                new AttributeDefinition { AttributeName = REMINDER_ID_PROPERTY_NAME, AttributeType = ScalarAttributeType.S },
                new AttributeDefinition { AttributeName = GRAIN_HASH_PROPERTY_NAME, AttributeType = ScalarAttributeType.N },
                new AttributeDefinition { AttributeName = SERVICE_ID_PROPERTY_NAME, AttributeType = ScalarAttributeType.S },
                new AttributeDefinition { AttributeName = GRAIN_REFERENCE_PROPERTY_NAME, AttributeType = ScalarAttributeType.S }
            },
            new List<GlobalSecondaryIndex> { serviceIdGrainHashGlobalSecondaryIndex, serviceIdGrainReferenceGlobalSecondaryIndex },
            cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Remove one row from the reminder table
    /// </summary>
    /// <param name="grainId"> specific grain ref to locate the row </param>
    /// <param name="reminderName"> reminder name to locate the row </param>
    /// <param name="eTag"> e tag </param>
    /// <returns> Return true if the row was removed </returns>
    public async Task<bool> RemoveRow(GrainId grainId, string reminderName, string eTag)
    {
        var reminderId = ConstructReminderId(this.serviceId, grainId, reminderName);

        var keys = new Dictionary<string, AttributeValue>
            {
                { $"{REMINDER_ID_PROPERTY_NAME}", new AttributeValue(reminderId) },
                { $"{GRAIN_HASH_PROPERTY_NAME}", new AttributeValue { N = grainId.GetUniformHashCode().ToString() } }
            };

        try
        {
            if (!TryCreateETagValue(eTag, out var eTagValue))
            {
                return false;
            }

            var conditionalValues = new Dictionary<string, AttributeValue> { { CURRENT_ETAG_ALIAS, eTagValue } };
            var expression = $"{ETAG_PROPERTY_NAME} = {CURRENT_ETAG_ALIAS}";

            await this.storage.DeleteEntryAsync(this.options.TableName, keys, expression, conditionalValues).ConfigureAwait(false);
            return true;
        }
        catch (ConditionalCheckFailedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Test hook to clear reminder table data.
    /// </summary>
    /// <returns></returns>
    public async Task TestOnlyClearTable()
    {
        var expressionValues = new Dictionary<string, AttributeValue>
            {
                { $":{SERVICE_ID_PROPERTY_NAME}", new AttributeValue(this.serviceId) }
            };

        try
        {
            var expression = $"{SERVICE_ID_PROPERTY_NAME} = :{SERVICE_ID_PROPERTY_NAME}";
            var records = await this.storage.ScanAsync(this.options.TableName, expressionValues, expression,
                item => new Dictionary<string, AttributeValue>
                {
                    { REMINDER_ID_PROPERTY_NAME, item[REMINDER_ID_PROPERTY_NAME] },
                    { GRAIN_HASH_PROPERTY_NAME, item[GRAIN_HASH_PROPERTY_NAME] }
                }).ConfigureAwait(false);

            if (records.Count <= 25)
            {
                await this.storage.DeleteEntriesAsync(this.options.TableName, records);
            }
            else
            {
                List<Task> tasks = new List<Task>();
                foreach (var batch in records.BatchIEnumerable(25))
                {
                    tasks.Add(this.storage.DeleteEntriesAsync(this.options.TableName, batch));
                }
                await Task.WhenAll(tasks);
            }
        }
        catch (Exception exc)
        {
            LogWarningRemoveReminderEntries(logger, exc, new(expressionValues), this.options.TableName);
            throw;
        }
    }

    /// <summary>
    /// Async method to put an entry into the reminder table
    /// </summary>
    /// <param name="entry"> The entry to put </param>
    /// <returns> Return the entry ETag if entry was upsert successfully </returns>
    public async Task<string> UpsertRow(ReminderEntry entry)
    {
        var reminderId = ConstructReminderId(this.serviceId, entry.GrainId, entry.ReminderName);

        var fields = new Dictionary<string, AttributeValue>
            {
                { REMINDER_ID_PROPERTY_NAME, new AttributeValue(reminderId) },
                { GRAIN_HASH_PROPERTY_NAME, new AttributeValue { N = entry.GrainId.GetUniformHashCode().ToString() } },
                { SERVICE_ID_PROPERTY_NAME, new AttributeValue(this.serviceId) },
                { GRAIN_REFERENCE_PROPERTY_NAME, new AttributeValue( entry.GrainId.ToString()) },
                { PERIOD_PROPERTY_NAME, new AttributeValue(entry.Period.ToString("c", CultureInfo.InvariantCulture)) },
                { START_TIME_PROPERTY_NAME, new AttributeValue(entry.StartAt.ToString("O", CultureInfo.InvariantCulture)) },
                { REMINDER_NAME_PROPERTY_NAME, new AttributeValue(entry.ReminderName) },
                { ACTION_PROPERTY_NAME, new AttributeValue { N = ((int)entry.Action).ToString(CultureInfo.InvariantCulture) } },
                { ETAG_PROPERTY_NAME, new AttributeValue(Guid.NewGuid().ToString("N")) }
            };

        if (!string.IsNullOrWhiteSpace(entry.CronExpression))
        {
            fields[CRON_EXPRESSION_PROPERTY_NAME] = new AttributeValue(entry.CronExpression);
        }

        if (!string.IsNullOrWhiteSpace(entry.CronTimeZoneId))
        {
            fields[CRON_TIME_ZONE_ID_PROPERTY_NAME] = new AttributeValue(entry.CronTimeZoneId);
        }

        if (entry.NextDueUtc is { } nextDueUtc)
        {
            fields[NEXT_DUE_UTC_PROPERTY_NAME] = new AttributeValue(nextDueUtc.ToString("O"));
        }

        if (entry.LastFireUtc is { } lastFireUtc)
        {
            fields[LAST_FIRE_UTC_PROPERTY_NAME] = new AttributeValue(lastFireUtc.ToString("O"));
        }

        if (!string.IsNullOrEmpty(entry.ScheduleId))
        {
            fields[SCHEDULE_ID_PROPERTY_NAME] = new AttributeValue(entry.ScheduleId);
        }

        if (!string.IsNullOrEmpty(entry.JobId))
        {
            fields[JOB_ID_PROPERTY_NAME] = new AttributeValue(entry.JobId);
        }

        if (!string.IsNullOrEmpty(entry.JobShardId))
        {
            fields[JOB_SHARD_ID_PROPERTY_NAME] = new AttributeValue(entry.JobShardId);
        }

        try
        {
            LogDebugUpsertRow(logger, entry, entry.ETag);

            if (string.IsNullOrEmpty(entry.ETag))
            {
                await this.storage.PutEntryAsync(
                    this.options.TableName,
                    fields,
                    $"attribute_not_exists({ETAG_PROPERTY_NAME})");
            }
            else
            {
                var conditionalValues = new Dictionary<string, AttributeValue>
                {
                    [CURRENT_ETAG_ALIAS] = CreateETagValue(entry.ETag),
                };
                await this.storage.PutEntryAsync(
                    this.options.TableName,
                    fields,
                    $"{ETAG_PROPERTY_NAME} = {CURRENT_ETAG_ALIAS}",
                    conditionalValues);
            }

            entry.ETag = ReadETag(fields[ETAG_PROPERTY_NAME]);
            return entry.ETag;
        }
        catch (ConditionalCheckFailedException)
        {
            throw new Runtime.ReminderException(
                $"Could not update reminder '{entry.ReminderName}' for grain '{entry.GrainId}' due to ETag mismatch.");
        }
        catch (Exception exc)
        {
            LogWarningUpdateReminderEntry(logger, exc, entry, options.TableName);
            throw;
        }
    }

    internal static string ConstructReminderId(string serviceId, GrainId grainId, string reminderName)
    {
        var grainIdText = grainId.ToString();
        var payload = string.Concat(
            serviceId.Length.ToString(CultureInfo.InvariantCulture),
            ":",
            serviceId,
            grainIdText.Length.ToString(CultureInfo.InvariantCulture),
            ":",
            grainIdText,
            reminderName.Length.ToString(CultureInfo.InvariantCulture),
            ":",
            reminderName);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }

    [LoggerMessage(
        EventId = (int)ErrorCode.ReminderServiceBase,
        Level = LogLevel.Information,
        Message = "Initializing AWS DynamoDB Reminders Table"
    )]
    private static partial void LogInformationInitializingDynamoDBRemindersTable(ILogger logger);

    private readonly struct DictionaryLogRecord(Dictionary<string, AttributeValue> keys)
    {
        public override string ToString() => Utils.DictionaryToString(keys) ?? string.Empty;
    }

    [LoggerMessage(
        EventId = (int)ErrorCode.ReminderServiceBase,
        Level = LogLevel.Warning,
        Message = "Intermediate error reading reminder entry {Keys} from table {TableName}."
    )]
    private static partial void LogWarningReadReminderEntry(ILogger logger, Exception exception, DictionaryLogRecord keys, string tableName);

    [LoggerMessage(
        EventId = (int)ErrorCode.ReminderServiceBase,
        Level = LogLevel.Warning,
        Message = "Intermediate error reading reminder entry {Entries} from table {TableName}."
    )]
    private static partial void LogWarningReadReminderEntries(ILogger logger, Exception exception, DictionaryLogRecord entries, string tableName);

    [LoggerMessage(
        EventId = (int)ErrorCode.ReminderServiceBase,
        Level = LogLevel.Warning,
        Message = "Intermediate error reading reminder entry {ExpressionValues} from table {TableName}."
    )]
    private static partial void LogWarningReadReminderEntryRange(ILogger logger, Exception exception, DictionaryLogRecord expressionValues, string tableName);

    [LoggerMessage(
        EventId = (int)ErrorCode.ReminderServiceBase,
        Level = LogLevel.Warning,
        Message = "Intermediate error removing reminder entries {Entries} from table {TableName}."
    )]
    private static partial void LogWarningRemoveReminderEntries(ILogger logger, Exception exception, DictionaryLogRecord entries, string tableName);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "UpsertRow entry = {Entry}, etag = {ETag}"
    )]
    private static partial void LogDebugUpsertRow(ILogger logger, ReminderEntry entry, string eTag);

    [LoggerMessage(
        EventId = (int)ErrorCode.ReminderServiceBase,
        Level = LogLevel.Warning,
        Message = "Intermediate error updating entry {Entry} to the table {TableName}."
    )]
    private static partial void LogWarningUpdateReminderEntry(ILogger logger, Exception exception, ReminderEntry entry, string tableName);
}
