using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.DurableJobs;
using Orleans.Runtime;

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
    private const string PRIORITY_PROPERTY_NAME = "Priority";
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
    public Task Init() => StartAsync(CancellationToken.None);

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
    /// Reads a reminder for a grain reference by reminder name.
    /// Read a row from the reminder table
    /// </summary>
    /// <param name="grainId"> grain ref to locate the row </param>
    /// <param name="reminderName"> reminder name to locate the row </param>
    /// <returns> Return the ReminderTableData if the rows were read successfully </returns>
    public async Task<ReminderEntry?> ReadRow(GrainId grainId, string reminderName)
    {
        var reminderId = ConstructReminderId(this.serviceId, grainId, reminderName);

        var keys = new Dictionary<string, AttributeValue>
            {
                { $"{REMINDER_ID_PROPERTY_NAME}", new AttributeValue(reminderId) },
                { $"{GRAIN_HASH_PROPERTY_NAME}", new AttributeValue { N = grainId.GetUniformHashCode().ToString() } }
            };

        try
        {
            return await this.storage.ReadSingleEntryAsync(this.options.TableName, keys, this.Resolve).ConfigureAwait(false);
        }
        catch (Exception exc)
        {
            LogWarningReadReminderEntry(logger, exc, new(keys), this.options.TableName);
            throw;
        }
    }

    /// <summary>
    /// Read one row from the reminder table
    /// </summary>
    /// <param name="grainId">grain ref to locate the row </param>
    /// <returns> Return the ReminderTableData if the rows were read successfully </returns>
    public async Task<ReminderTableData> ReadRows(GrainId grainId)
    {
        var expressionValues = new Dictionary<string, AttributeValue>
            {
                { $":{SERVICE_ID_PROPERTY_NAME}", new AttributeValue(this.serviceId) },
                { $":{GRAIN_REFERENCE_PROPERTY_NAME}", new AttributeValue(grainId.ToString()) }
            };

        try
        {
            var expression = $"{SERVICE_ID_PROPERTY_NAME} = :{SERVICE_ID_PROPERTY_NAME} AND {GRAIN_REFERENCE_PROPERTY_NAME} = :{GRAIN_REFERENCE_PROPERTY_NAME}";
            var records = await this.storage.QueryAllAsync(this.options.TableName, expressionValues, expression, this.Resolve, SERVICE_ID_GRAIN_REFERENCE_INDEX, consistentRead: false).ConfigureAwait(false);

            return new ReminderTableData(records);
        }
        catch (Exception exc)
        {
            LogWarningReadReminderEntries(logger, exc, new(expressionValues), this.options.TableName);
            throw;
        }
    }

    /// <summary>
    /// Reads reminder table data for a given hash range.
    /// </summary>
    /// <param name="begin"></param>
    /// <param name="end"></param>
    /// <returns> Return the RemiderTableData if the rows were read successfully </returns>
    public async Task<ReminderTableData> ReadRows(uint begin, uint end)
    {
        Dictionary<string, AttributeValue> expressionValues = new();

        try
        {
            string expression = string.Empty;
            List<ReminderEntry> records;

            if (begin < end)
            {
                expressionValues = new Dictionary<string, AttributeValue>
                {
                    { $":{SERVICE_ID_PROPERTY_NAME}", new AttributeValue(this.serviceId) },
                    { $":Begin{GRAIN_HASH_PROPERTY_NAME}", new AttributeValue { N = (begin + 1).ToString() } },
                    { $":End{GRAIN_HASH_PROPERTY_NAME}", new AttributeValue { N = end.ToString() } }
                };
                expression = $"{SERVICE_ID_PROPERTY_NAME} = :{SERVICE_ID_PROPERTY_NAME} AND {GRAIN_HASH_PROPERTY_NAME} BETWEEN :Begin{GRAIN_HASH_PROPERTY_NAME} AND :End{GRAIN_HASH_PROPERTY_NAME}";
                records = await this.storage.QueryAllAsync(this.options.TableName, expressionValues, expression, this.Resolve, SERVICE_ID_GRAIN_HASH_INDEX, consistentRead: false).ConfigureAwait(false);
            }

            else
            {
                expressionValues = new Dictionary<string, AttributeValue>
                {
                    { $":{SERVICE_ID_PROPERTY_NAME}", new AttributeValue(this.serviceId) },
                    { $":End{GRAIN_HASH_PROPERTY_NAME}", new AttributeValue { N = end.ToString() } }
                };
                expression = $"{SERVICE_ID_PROPERTY_NAME} = :{SERVICE_ID_PROPERTY_NAME} AND {GRAIN_HASH_PROPERTY_NAME} <= :End{GRAIN_HASH_PROPERTY_NAME}";
                records = await this.storage.QueryAllAsync(this.options.TableName, expressionValues, expression, this.Resolve, SERVICE_ID_GRAIN_HASH_INDEX, consistentRead: false).ConfigureAwait(false);

                expressionValues = new Dictionary<string, AttributeValue>
                {
                    { $":{SERVICE_ID_PROPERTY_NAME}", new AttributeValue(this.serviceId) },
                    { $":Begin{GRAIN_HASH_PROPERTY_NAME}", new AttributeValue { N = begin.ToString() } }
                };
                expression = $"{SERVICE_ID_PROPERTY_NAME} = :{SERVICE_ID_PROPERTY_NAME} AND {GRAIN_HASH_PROPERTY_NAME} > :Begin{GRAIN_HASH_PROPERTY_NAME}";
                records.AddRange(await this.storage.QueryAllAsync(this.options.TableName, expressionValues, expression, this.Resolve, SERVICE_ID_GRAIN_HASH_INDEX, consistentRead: false).ConfigureAwait(false));

            }

            return new ReminderTableData(records);
        }
        catch (Exception exc)
        {
            LogWarningReadReminderEntryRange(logger, exc, new(expressionValues), this.options.TableName);
            throw;
        }
    }

    public async Task<ReminderTableData> ReadRows(uint begin, uint end, int maxRows, string? continuationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxRows);
        var token = ParseContinuationToken(continuationToken);
        var records = new List<ReminderEntry>(maxRows);
        var wraps = begin >= end;
        try
        {
            while (records.Count < maxRows)
            {
                Dictionary<string, AttributeValue> expressionValues;
                string expression;
                if (!wraps || token.Phase == 0)
                {
                    expressionValues = new Dictionary<string, AttributeValue>
                    {
                        [$":{SERVICE_ID_PROPERTY_NAME}"] = new AttributeValue(serviceId),
                        [$":Begin{GRAIN_HASH_PROPERTY_NAME}"] = new AttributeValue { N = (wraps ? begin : begin + 1).ToString(CultureInfo.InvariantCulture) },
                    };
                    expression = $"{SERVICE_ID_PROPERTY_NAME} = :{SERVICE_ID_PROPERTY_NAME} AND {GRAIN_HASH_PROPERTY_NAME} {(wraps ? ">" : "BETWEEN")} :Begin{GRAIN_HASH_PROPERTY_NAME}";
                    if (!wraps)
                    {
                        expressionValues[$":End{GRAIN_HASH_PROPERTY_NAME}"] = new AttributeValue { N = end.ToString(CultureInfo.InvariantCulture) };
                        expression += $" AND :End{GRAIN_HASH_PROPERTY_NAME}";
                    }
                }
                else if (token.Phase == 1)
                {
                    expressionValues = new Dictionary<string, AttributeValue>
                    {
                        [$":{SERVICE_ID_PROPERTY_NAME}"] = new AttributeValue(serviceId),
                        [$":End{GRAIN_HASH_PROPERTY_NAME}"] = new AttributeValue { N = end.ToString(CultureInfo.InvariantCulture) },
                    };
                    expression = $"{SERVICE_ID_PROPERTY_NAME} = :{SERVICE_ID_PROPERTY_NAME} AND {GRAIN_HASH_PROPERTY_NAME} <= :End{GRAIN_HASH_PROPERTY_NAME}";
                }
                else
                {
                    break;
                }

                var remaining = maxRows - records.Count;
                var page = await storage.QueryAsync(
                    options.TableName,
                    expressionValues,
                    expression,
                    Resolve,
                    SERVICE_ID_GRAIN_HASH_INDEX,
                    scanIndexForward: true,
                    lastEvaluatedKey: token.LastEvaluatedKey,
                    consistentRead: false,
                    limit: remaining).ConfigureAwait(false);
                records.AddRange(page.results);
                if (page.lastEvaluatedKey is { Count: > 0 })
                {
                    return new ReminderTableData(records, FormatContinuationToken(token.Phase, page.lastEvaluatedKey));
                }

                if (!wraps || token.Phase == 1)
                {
                    break;
                }

                token = new DynamoContinuationToken { Phase = 1 };
                if (records.Count == maxRows)
                {
                    return new ReminderTableData(records, CreatePhaseContinuationToken(token.Phase));
                }
            }

            return new ReminderTableData(records);
        }
        catch (Exception exc) when (exc is not ArgumentException and not ArgumentOutOfRangeException)
        {
            LogWarningReadReminderEntryRange(
                logger,
                exc,
                new DictionaryLogRecord(new Dictionary<string, AttributeValue>()),
                options.TableName);
            throw;
        }
    }

    private static DynamoContinuationToken ParseContinuationToken(string? continuationToken)
    {
        if (continuationToken is null)
        {
            return new DynamoContinuationToken();
        }

        try
        {
            var bytes = Convert.FromBase64String(continuationToken);
            var result = JsonSerializer.Deserialize<DynamoContinuationToken>(bytes);
            if (result is null || result.Phase is < 0 or > 1)
            {
                throw new FormatException();
            }

            return result;
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            throw new ArgumentException("The continuation token is invalid.", nameof(continuationToken), exception);
        }
    }

    private static string FormatContinuationToken(int phase, Dictionary<string, AttributeValue> lastEvaluatedKey)
    {
        var values = new Dictionary<string, DynamoContinuationValue>(StringComparer.Ordinal);
        foreach (var (key, value) in lastEvaluatedKey)
        {
            values[key] = new DynamoContinuationValue { S = value.S, N = value.N };
        }

        return Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new DynamoContinuationToken
        {
            Phase = phase,
            Values = values,
        }));
    }

    internal static string CreatePhaseContinuationToken(int phase)
        => Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new DynamoContinuationToken
        {
            Phase = phase,
        }));

    internal static int GetContinuationPhase(string continuationToken)
        => ParseContinuationToken(continuationToken).Phase;

    private sealed class DynamoContinuationToken
    {
        public int Phase { get; set; }

        public Dictionary<string, DynamoContinuationValue>? Values { get; set; }

        [JsonIgnore]
        public Dictionary<string, AttributeValue>? LastEvaluatedKey
            => Values?.ToDictionary(
                static pair => pair.Key,
                static pair => new AttributeValue { S = pair.Value.S, N = pair.Value.N },
                StringComparer.Ordinal);
    }

    private sealed class DynamoContinuationValue
    {
        public string? S { get; set; }

        public string? N { get; set; }
    }

    private ReminderEntry Resolve(Dictionary<string, AttributeValue> item)
    {
        return new ReminderEntry
        {
            ETag = ReadETag(item[ETAG_PROPERTY_NAME]),
            GrainId = GrainId.Parse(item[GRAIN_REFERENCE_PROPERTY_NAME].S),
            Period = TimeSpan.Parse(item[PERIOD_PROPERTY_NAME].S, CultureInfo.InvariantCulture),
            CronExpression = ReadOptionalString(item, CRON_EXPRESSION_PROPERTY_NAME),
            CronTimeZoneId = ReadOptionalString(item, CRON_TIME_ZONE_ID_PROPERTY_NAME),
            NextDueUtc = ReadOptionalDateTime(item, NEXT_DUE_UTC_PROPERTY_NAME),
            LastFireUtc = ReadOptionalDateTime(item, LAST_FIRE_UTC_PROPERTY_NAME),
            ScheduleId = ReadOptionalString(item, SCHEDULE_ID_PROPERTY_NAME),
            JobId = ReadOptionalString(item, JOB_ID_PROPERTY_NAME),
            JobShardId = ReadOptionalString(item, JOB_SHARD_ID_PROPERTY_NAME),
            Priority = ReadPriority(item),
            Action = ReadAction(item),
            ReminderName = item[REMINDER_NAME_PROPERTY_NAME].S,
            StartAt = DateTime.Parse(item[START_TIME_PROPERTY_NAME].S, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
        };
    }

    private static string ReadOptionalString(Dictionary<string, AttributeValue> item, string propertyName)
        => item.TryGetValue(propertyName, out var value) ? value.S ?? string.Empty : string.Empty;

    private static string ReadETag(AttributeValue value)
        => !string.IsNullOrEmpty(value.S) ? value.S : value.N;

    internal static bool TryCreateETagValue(string eTag, out AttributeValue value)
    {
        if (Guid.TryParseExact(eTag, "N", out _))
        {
            value = new AttributeValue(eTag);
            return true;
        }

        if (long.TryParse(eTag, NumberStyles.Integer, CultureInfo.InvariantCulture, out var legacyETag)
            && string.Equals(eTag, legacyETag.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
        {
            value = new AttributeValue { N = eTag };
            return true;
        }

        value = null!;
        return false;
    }

    private static AttributeValue CreateETagValue(string eTag)
        => TryCreateETagValue(eTag, out var value)
            ? value
            : throw new FormatException($"ETag '{eTag}' is neither a GUID nor a legacy numeric ETag.");

    private static DateTime? ReadOptionalDateTime(Dictionary<string, AttributeValue> item, string propertyName)
    {
        if (!item.TryGetValue(propertyName, out var value) || string.IsNullOrWhiteSpace(value.S))
        {
            return null;
        }

        return DateTime.Parse(value.S, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    }

    private static DurableJobPriority ReadPriority(Dictionary<string, AttributeValue> item)
    {
        if (!TryReadInt32(item, PRIORITY_PROPERTY_NAME, out var value))
        {
            return DurableJobPriority.Normal;
        }

        return ParsePriority(value);
    }

    private static MissedReminderAction ReadAction(Dictionary<string, AttributeValue> item)
    {
        if (!TryReadInt32(item, ACTION_PROPERTY_NAME, out var value))
        {
            return MissedReminderAction.Skip;
        }

        return ParseAction(value);
    }

    private static bool TryReadInt32(Dictionary<string, AttributeValue> item, string propertyName, out int value)
    {
        value = default;
        return item.TryGetValue(propertyName, out var attributeValue)
            && !string.IsNullOrWhiteSpace(attributeValue.N)
            && int.TryParse(attributeValue.N, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static DurableJobPriority ParsePriority(int value) => value switch
    {
        (int)DurableJobPriority.Low => DurableJobPriority.Low,
        (int)DurableJobPriority.High => DurableJobPriority.High,
        (int)DurableJobPriority.Normal => DurableJobPriority.Normal,
        _ => DurableJobPriority.Normal,
    };

    private static MissedReminderAction ParseAction(int value) => value switch
    {
        (int)MissedReminderAction.FireImmediately => MissedReminderAction.FireImmediately,
        (int)MissedReminderAction.Skip => MissedReminderAction.Skip,
        (int)MissedReminderAction.Notify => MissedReminderAction.Notify,
        _ => MissedReminderAction.Skip,
    };

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
                { PRIORITY_PROPERTY_NAME, new AttributeValue { N = ((int)entry.Priority).ToString(CultureInfo.InvariantCulture) } },
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
