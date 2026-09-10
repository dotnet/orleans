using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Amazon.DynamoDBv2.Model;

namespace Orleans.AdvancedReminders.DynamoDB;

internal sealed partial class DynamoDBReminderTable
{
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
}
