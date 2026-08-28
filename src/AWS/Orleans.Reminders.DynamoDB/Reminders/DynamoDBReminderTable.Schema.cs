using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Orleans.Runtime;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Orleans.Reminders.DynamoDB;

internal sealed partial class DynamoDBReminderTable
{
    internal const int V2BucketCount = 32;
    internal const string V2PartitionKeyName = "PartitionKey";
    internal const string V2SortKeyName = "SortKey";

    private const string DataPartitionPrefix = "D#";
    private const string MetadataPartitionPrefix = "M#";
    private const string ReminderSortPrefix = "R#";

    private async Task InitializeV2Table(CancellationToken cancellationToken)
    {
        await storage.InitializeTable(
            v2TableName,
            [
                new() { AttributeName = V2PartitionKeyName, KeyType = KeyType.HASH },
                new() { AttributeName = V2SortKeyName, KeyType = KeyType.RANGE },
            ],
            [
                new() { AttributeName = V2PartitionKeyName, AttributeType = ScalarAttributeType.S },
                new() { AttributeName = V2SortKeyName, AttributeType = ScalarAttributeType.S },
            ],
            cancellationToken: cancellationToken);
    }

    internal static string GetV2PartitionKey(string serviceId, uint grainHash)
        => $"{DataPartitionPrefix}{Encode(serviceId)}#{grainHash % V2BucketCount:X2}";

    private string GetV2PartitionKey(uint grainHash) => $"{v2DataPartitionPrefix}{grainHash % V2BucketCount:X2}";

    internal static string GetV2SortKey(uint grainHash, GrainId grainId, string reminderName)
        => $"{GetV2GrainPrefix(grainHash, grainId)}{EncodeKeyComponent(reminderName, 600)}";

    internal static string GetV2GrainPrefix(uint grainHash, GrainId grainId)
        => $"{ReminderSortPrefix}{grainHash:X8}#{EncodeKeyComponent(grainId.ToString(), 300)}#";

    internal static (string Lower, string Upper) GetV2RangeBounds(uint lowerInclusive, uint upperInclusive)
        => ($"{ReminderSortPrefix}{lowerInclusive:X8}#", $"{ReminderSortPrefix}{upperInclusive:X8}#~");

    private static string Encode(string value) => EncodeKeyComponent(value, 300);

    private static string EncodeKeyComponent(string value, int maximumEncodedLength)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        var encoded = Base64Url(bytes);
        return encoded.Length <= maximumEncodedLength
            ? $"V{encoded}"
            : $"H{Base64Url(SHA256.HashData(bytes))}";
    }

    private static string Base64Url(ReadOnlySpan<byte> value)
        => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private Dictionary<string, AttributeValue> GetLegacyKey(GrainId grainId, string reminderName)
        => new()
        {
            [REMINDER_ID_PROPERTY_NAME] = new(ConstructReminderId(serviceId, grainId, reminderName)),
            [GRAIN_HASH_PROPERTY_NAME] = new() { N = grainId.GetUniformHashCode().ToString(CultureInfo.InvariantCulture) },
        };

    private Dictionary<string, AttributeValue> GetV2Key(GrainId grainId, string reminderName)
    {
        var hash = grainId.GetUniformHashCode();
        return new()
        {
            [V2PartitionKeyName] = new(GetV2PartitionKey(hash)),
            [V2SortKeyName] = new(GetV2SortKey(hash, grainId, reminderName)),
        };
    }

    private Dictionary<string, AttributeValue> CreateLegacyItem(ReminderEntry entry, string etag)
        => new()
        {
            [REMINDER_ID_PROPERTY_NAME] = new(ConstructReminderId(serviceId, entry.GrainId, entry.ReminderName)),
            [GRAIN_HASH_PROPERTY_NAME] = new() { N = entry.GrainId.GetUniformHashCode().ToString(CultureInfo.InvariantCulture) },
            [SERVICE_ID_PROPERTY_NAME] = new(serviceId),
            [GRAIN_REFERENCE_PROPERTY_NAME] = new(entry.GrainId.ToString()),
            [PERIOD_PROPERTY_NAME] = new(entry.Period.ToString()),
            [START_TIME_PROPERTY_NAME] = new(entry.StartAt.ToString("O", CultureInfo.InvariantCulture)),
            [REMINDER_NAME_PROPERTY_NAME] = new(entry.ReminderName),
            [ETAG_PROPERTY_NAME] = new() { N = etag },
        };

    private Dictionary<string, AttributeValue> CreateV2Item(ReminderEntry entry, string etag)
    {
        var item = CreateLegacyItem(entry, etag);
        item.Remove(REMINDER_ID_PROPERTY_NAME);
        var hash = entry.GrainId.GetUniformHashCode();
        item[V2PartitionKeyName] = new(GetV2PartitionKey(hash));
        item[V2SortKeyName] = new(GetV2SortKey(hash, entry.GrainId, entry.ReminderName));
        return item;
    }

    private async Task<ReminderEntry?> ReadV2Row(GrainId grainId, string reminderName)
        => await storage.ReadSingleEntryAsync(v2TableName, GetV2Key(grainId, reminderName), Resolve);

    private async Task<ReminderTableData> ReadV2Rows(GrainId grainId)
    {
        var hash = grainId.GetUniformHashCode();
        var values = new Dictionary<string, AttributeValue>
        {
            [":partition"] = new(GetV2PartitionKey(hash)),
            [":prefix"] = new(GetV2GrainPrefix(hash, grainId)),
        };
        var rows = await storage.QueryAllAsync(
            v2TableName,
            values,
            $"{V2PartitionKeyName} = :partition AND begins_with({V2SortKeyName}, :prefix)",
            Resolve);
        return new(rows);
    }

    private async Task<ReminderTableData> ReadV2Rows(uint begin, uint end)
    {
        var tasks = new List<Task<List<ReminderEntry>>>();
        for (var bucket = 0; bucket < V2BucketCount; bucket++)
        {
            if (begin < end)
            {
                tasks.Add(QueryV2RangeBucket(bucket, begin + 1, end));
            }
            else
            {
                if (begin != uint.MaxValue)
                {
                    tasks.Add(QueryV2RangeBucket(bucket, begin + 1, uint.MaxValue));
                }

                tasks.Add(QueryV2RangeBucket(bucket, 0, end));
            }
        }

        var pages = await Task.WhenAll(tasks);
        return new(pages.SelectMany(static page => page));
    }

    private Task<List<ReminderEntry>> QueryV2RangeBucket(int bucket, uint lowerInclusive, uint upperInclusive)
    {
        var bounds = GetV2RangeBounds(lowerInclusive, upperInclusive);
        var values = new Dictionary<string, AttributeValue>
        {
            [":partition"] = new($"{v2DataPartitionPrefix}{bucket:X2}"),
            [":lower"] = new(bounds.Lower),
            [":upper"] = new(bounds.Upper),
        };
        return storage.QueryAllAsync(
            v2TableName,
            values,
            $"{V2PartitionKeyName} = :partition AND {V2SortKeyName} BETWEEN :lower AND :upper",
            Resolve);
    }

    private async Task<string?> UpsertDualRow(ReminderEntry entry)
    {
        var etag = Random.Shared.NextInt64(1, long.MaxValue).ToString(CultureInfo.InvariantCulture);
        var legacy = CreateLegacyItem(entry, etag);
        var v2 = CreateV2Item(entry, etag);
        try
        {
            await storage.WriteTxAsync(
            [
                new() { ConditionCheck = CreateDualWriteFence() },
                new()
                {
                    Put = new() { TableName = options.TableName, Item = legacy },
                },
                new()
                {
                    Put = CreateIdentitySafeV2Put(v2, entry),
                },
            ]);
        }
        catch (TransactionCanceledException exception) when (IsConditionalFailureAt(exception, 0))
        {
            await RefreshReadMode();
            if (useV2OnlyWrites)
            {
                return await UpsertV2OnlyRow(entry);
            }

            throw;
        }
        catch (TransactionCanceledException exception) when (IsTransactionConflict(exception))
        {
            await RefreshReadMode();
            if (useV2OnlyWrites)
            {
                return await UpsertV2OnlyRow(entry);
            }

            throw;
        }

        entry.ETag = etag;
        return etag;
    }

    private async Task<string?> UpsertV2OnlyRow(ReminderEntry entry)
    {
        var etag = Random.Shared.NextInt64(1, long.MaxValue).ToString(CultureInfo.InvariantCulture);
        await storage.WriteTxAsync(
        [
            new()
            {
                Put = CreateIdentitySafeV2Put(CreateV2Item(entry, etag), entry),
            },
        ]);
        entry.ETag = etag;
        return etag;
    }

    private Put CreateIdentitySafeV2Put(Dictionary<string, AttributeValue> item, ReminderEntry entry)
        => new()
        {
            TableName = v2TableName,
            Item = item,
            ConditionExpression = $"attribute_not_exists({V2PartitionKeyName}) OR ({SERVICE_ID_PROPERTY_NAME} = :service AND {GRAIN_REFERENCE_PROPERTY_NAME} = :grain AND {REMINDER_NAME_PROPERTY_NAME} = :reminder)",
            ExpressionAttributeValues = new()
            {
                [":service"] = new(serviceId),
                [":grain"] = new(entry.GrainId.ToString()),
                [":reminder"] = new(entry.ReminderName),
            },
        };

    private async Task<bool> RemoveDualRow(GrainId grainId, string reminderName, string etag)
    {
        var values = new Dictionary<string, AttributeValue> { [CURRENT_ETAG_ALIAS] = new() { N = etag } };
        try
        {
            await storage.WriteTxAsync(
            [
                new() { ConditionCheck = CreateDualWriteFence() },
                new()
                {
                    Delete = new()
                    {
                        TableName = options.TableName,
                        Key = GetLegacyKey(grainId, reminderName),
                        ConditionExpression = $"{ETAG_PROPERTY_NAME} = {CURRENT_ETAG_ALIAS}",
                        ExpressionAttributeValues = values,
                    },
                },
                new()
                {
                    Delete = new()
                    {
                        TableName = v2TableName,
                        Key = GetV2Key(grainId, reminderName),
                    },
                },
            ]);
            return true;
        }
        catch (TransactionCanceledException exception) when (IsConditionalFailureAt(exception, 0))
        {
            await RefreshReadMode();
            if (useV2OnlyWrites)
            {
                return await RemoveV2OnlyRow(grainId, reminderName, etag);
            }

            throw;
        }
        catch (TransactionCanceledException exception) when (IsTransactionConflict(exception))
        {
            await RefreshReadMode();
            if (useV2OnlyWrites)
            {
                return await RemoveV2OnlyRow(grainId, reminderName, etag);
            }

            throw;
        }
        catch (TransactionCanceledException exception) when (IsConditionalFailure(exception))
        {
            return false;
        }
    }

    private async Task<bool> RemoveV2OnlyRow(GrainId grainId, string reminderName, string etag)
    {
        try
        {
            await storage.WriteTxAsync(
            [
                new()
                {
                    Delete = new()
                    {
                        TableName = v2TableName,
                        Key = GetV2Key(grainId, reminderName),
                        ConditionExpression = $"{ETAG_PROPERTY_NAME} = {CURRENT_ETAG_ALIAS}",
                        ExpressionAttributeValues = new()
                        {
                            [CURRENT_ETAG_ALIAS] = new() { N = etag },
                        },
                    },
                },
            ]);
            return true;
        }
        catch (TransactionCanceledException exception) when (IsConditionalFailure(exception))
        {
            return false;
        }
    }

    private ConditionCheck CreateDualWriteFence()
        => new()
        {
            TableName = v2TableName,
            Key = MetadataKey(MigrationStateSortKey),
            ConditionExpression = $"{StatusAttribute} <> :retired",
            ExpressionAttributeValues = new()
            {
                [":retired"] = new(MigrationStatus.Retired.ToString()),
            },
        };

    private async Task ClearV2ServiceRows()
    {
        var keys = new List<Dictionary<string, AttributeValue>>();
        for (var bucket = 0; bucket < V2BucketCount; bucket++)
        {
            var values = new Dictionary<string, AttributeValue>
            {
                [":partition"] = new($"{v2DataPartitionPrefix}{bucket:X2}"),
            };
            keys.AddRange(await storage.QueryAllAsync(
                v2TableName,
                values,
                $"{V2PartitionKeyName} = :partition",
                static item => new Dictionary<string, AttributeValue>
                {
                    [V2PartitionKeyName] = item[V2PartitionKeyName],
                    [V2SortKeyName] = item[V2SortKeyName],
                }));
        }

        foreach (var batch in keys.BatchIEnumerable(25))
        {
            await storage.DeleteEntriesAsync(v2TableName, batch);
        }
    }

    private static bool IsConditionalFailure(TransactionCanceledException exception)
        => exception.CancellationReasons?.Any(static reason => reason.Code == "ConditionalCheckFailed") == true;

    private static bool IsConditionalFailureAt(TransactionCanceledException exception, int index)
        => exception.CancellationReasons is { } reasons
            && reasons.Count > index
            && reasons[index].Code == "ConditionalCheckFailed";

    private static bool IsTransactionConflict(TransactionCanceledException exception)
        => exception.CancellationReasons?.Any(static reason => reason.Code == "TransactionConflict") == true;
}
