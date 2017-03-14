using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;
using Orleans;
using Orleans.Runtime;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace OrleansAWSUtils.Storage
{
    /// <summary>
    /// Wrapper around AWS DynamoDB SDK.
    /// </summary>
    public class DynamoDBStorage
    {
        private const string AccessKeyPropertyName = "AccessKey";
        private const string SecretKeyPropertyName = "SecretKey";
        private const string ServicePropertyName = "Service";
        private const string ReadCapacityUnitsPropertyName = "ReadCapacityUnits";
        private const string WriteCapacityUnitsPropertyName = "WriteCapacityUnits";

        private string accessKey;

        /// <summary> Secret key for this dynamoDB table </summary>
        protected string secretKey;
        private string service;
        private int readCapacityUnits = 10;
        private int writeCapacityUnits = 5;
        private AmazonDynamoDBClient ddbClient;
        private Logger Logger;

        /// <summary>
        /// Create a DynamoDBStorage instance
        /// </summary>
        /// <param name="dataConnectionString">The connection string to be parsed for DynamoDB connection settings</param>
        /// <param name="logger">Orleans Logger instance</param>
        public DynamoDBStorage(string dataConnectionString, Logger logger = null)
        {
            ParseDataConnectionString(dataConnectionString);
            Logger = logger ?? LogManager.GetLogger($"DynamoDBStorage", LoggerType.Runtime);
            CreateClient();
        }

        /// <summary>
        /// Create a DynamoDB table if it doesn't exist
        /// </summary>
        /// <param name="tableName">The name of the table</param>
        /// <param name="keys">The keys definitions</param>
        /// <param name="attributes">The attributes used on the key definition</param>
        /// <param name="secondaryIndexes">(optional) The secondary index definitions</param>
        /// <returns></returns>
        public async Task InitializeTable(string tableName, List<KeySchemaElement> keys, List<AttributeDefinition> attributes, List<GlobalSecondaryIndex> secondaryIndexes = null)
        {
            try
            {
                if (await GetTableDescription(tableName) == null)
                    await CreateTable(tableName, keys, attributes, secondaryIndexes);
            }
            catch (Exception exc)
            {
                Logger.Error(ErrorCode.StorageProviderBase, $"Could not initialize connection to storage table {tableName}", exc);
                throw;
            }
        }

        #region Table Management Operations

        private void ParseDataConnectionString(string dataConnectionString)
        {
            var parameters = dataConnectionString.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);

            var serviceConfig = parameters.Where(p => p.Contains(ServicePropertyName)).FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(serviceConfig))
            {
                var value = serviceConfig.Split(new[] { '=' }, StringSplitOptions.RemoveEmptyEntries);
                if (value.Length == 2 && !string.IsNullOrWhiteSpace(value[1]))
                    service = value[1];
            }

            var secretKeyConfig = parameters.Where(p => p.Contains(SecretKeyPropertyName)).FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(secretKeyConfig))
            {
                var value = secretKeyConfig.Split(new[] { '=' }, StringSplitOptions.RemoveEmptyEntries);
                if (value.Length == 2 && !string.IsNullOrWhiteSpace(value[1]))
                    secretKey = value[1];
            }

            var accessKeyConfig = parameters.Where(p => p.Contains(AccessKeyPropertyName)).FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(accessKeyConfig))
            {
                var value = accessKeyConfig.Split(new[] { '=' }, StringSplitOptions.RemoveEmptyEntries);
                if (value.Length == 2 && !string.IsNullOrWhiteSpace(value[1]))
                    accessKey = value[1];
            }

            var readCapacityUnitsConfig = parameters.Where(p => p.Contains(ReadCapacityUnitsPropertyName)).FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(readCapacityUnitsConfig))
            {
                var value = readCapacityUnitsConfig.Split(new[] { '=' }, StringSplitOptions.RemoveEmptyEntries);
                if (value.Length == 2 && !string.IsNullOrWhiteSpace(value[1]))
                    readCapacityUnits = int.Parse(value[1]);
            }

            var writeCapacityUnitsConfig = parameters.Where(p => p.Contains(WriteCapacityUnitsPropertyName)).FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(writeCapacityUnitsConfig))
            {
                var value = writeCapacityUnitsConfig.Split(new[] { '=' }, StringSplitOptions.RemoveEmptyEntries);
                if (value.Length == 2 && !string.IsNullOrWhiteSpace(value[1]))
                    writeCapacityUnits = int.Parse(value[1]);
            }
        }

        private void CreateClient()
        {
            if (service.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                service.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                var credentials = new BasicAWSCredentials("dummy", "dummyKey");
                ddbClient = new AmazonDynamoDBClient(credentials, new AmazonDynamoDBConfig { ServiceURL = service });
            }
            else
            {
                var credentials = new BasicAWSCredentials(accessKey, secretKey);
                ddbClient = new AmazonDynamoDBClient(credentials, new AmazonDynamoDBConfig { ServiceURL = service, RegionEndpoint = AWSUtils.GetRegionEndpoint(service) });
            }
        }

        private async Task<TableDescription> GetTableDescription(string tableName)
        {
            try
            {
                var description = await ddbClient.DescribeTableAsync(tableName);
                if (description.Table != null)
                    return description.Table;
            }
            catch (ResourceNotFoundException)
            {
                return null;
            }
            return null;
        }

        private async Task CreateTable(string tableName, List<KeySchemaElement> keys, List<AttributeDefinition> attributes, List<GlobalSecondaryIndex> secondaryIndexes = null)
        {
            var request = new CreateTableRequest
            {
                TableName = tableName,
                AttributeDefinitions = attributes,
                KeySchema = keys,
                ProvisionedThroughput = new ProvisionedThroughput
                {
                    ReadCapacityUnits = readCapacityUnits,
                    WriteCapacityUnits = writeCapacityUnits
                }
            };

            if (secondaryIndexes != null && secondaryIndexes.Count > 0)
            {
                var indexThroughput = new ProvisionedThroughput { ReadCapacityUnits = readCapacityUnits, WriteCapacityUnits = writeCapacityUnits };
                secondaryIndexes.ForEach(i =>
                {
                    i.ProvisionedThroughput = indexThroughput;
                });
                request.GlobalSecondaryIndexes = secondaryIndexes;
            }

            try
            {
                var response = await ddbClient.CreateTableAsync(request);
                TableDescription description = null;
                do
                {
                    description = await GetTableDescription(tableName);

                    await Task.Delay(2000);

                } while (description.TableStatus == TableStatus.CREATING);

                if (description.TableStatus != TableStatus.ACTIVE)
                    throw new InvalidOperationException($"Failure creating table {tableName}");
            }
            catch (Exception exc)
            {
                Logger.Error(ErrorCode.StorageProviderBase, $"Could not create table {tableName}", exc);
                throw;
            }
        }

        /// <summary>
        /// Delete a table from DynamoDB
        /// </summary>
        /// <param name="tableName">The name of the table to delete</param>
        /// <returns></returns>
        public Task DeleTableAsync(string tableName)
        {
            try
            {
                return ddbClient.DeleteTableAsync(new DeleteTableRequest { TableName = tableName });
            }
            catch (Exception exc)
            {
                Logger.Error(ErrorCode.StorageProviderBase, $"Could not delete table {tableName}", exc);
                throw;
            }
        }

        #endregion

        #region CRUD

        /// <summary>
        /// Create or Replace an entry in a DynamoDB Table
        /// </summary>
        /// <param name="tableName">The name of the table to put an entry</param>
        /// <param name="fields">The fields/attributes to add or replace in the table</param>
        /// <param name="conditionExpression">Optional conditional expression</param>
        /// <param name="conditionValues">Optional field/attribute values used in the conditional expression</param>
        /// <returns></returns>
        public Task PutEntryAsync(string tableName, Dictionary<string, AttributeValue> fields, string conditionExpression = "", Dictionary<string, AttributeValue> conditionValues = null)
        {
            if (Logger.IsVerbose2) Logger.Verbose2("Creating {0} table entry: {1}", tableName, Utils.DictionaryToString(fields));

            try
            {
                var request = new PutItemRequest(tableName, fields, ReturnValue.NONE);
                if (!string.IsNullOrWhiteSpace(conditionExpression))
                    request.ConditionExpression = conditionExpression;

                if (conditionValues != null && conditionValues.Keys.Count > 0)
                    request.ExpressionAttributeValues = conditionValues;

                return ddbClient.PutItemAsync(request);
            }
            catch (Exception exc)
            {
                Logger.Error(ErrorCode.StorageProviderBase, $"Unable to create item to table '{tableName}'", exc);
                throw;
            }
        }

        /// <summary>
        /// Create or update an entry in a DynamoDB Table
        /// </summary>
        /// <param name="tableName">The name of the table to upsert an entry</param>
        /// <param name="keys">The table entry keys for the entry</param>
        /// <param name="fields">The fields/attributes to add or updated in the table</param>
        /// <param name="conditionExpression">Optional conditional expression</param>
        /// <param name="conditionValues">Optional field/attribute values used in the conditional expression</param>
        /// <param name="extraExpression">Additional expression that will be added in the end of the upsert expression</param>
        /// <param name="extraExpressionValues">Additional field/attribute that will be used in the extraExpression</param>
        /// <remarks>The fields dictionary item values will be updated with the values returned from DynamoDB</remarks>
        /// <returns></returns>
        public async Task UpsertEntryAsync(string tableName, Dictionary<string, AttributeValue> keys, Dictionary<string, AttributeValue> fields,
            string conditionExpression = "", Dictionary<string, AttributeValue> conditionValues = null, string extraExpression = "",
            Dictionary<string, AttributeValue> extraExpressionValues = null)
        {
            if (Logger.IsVerbose2) Logger.Verbose2("Upserting entry {0} with key(s) {1} into table {2}", Utils.DictionaryToString(fields), Utils.DictionaryToString(keys), tableName);

            try
            {
                var request = new UpdateItemRequest
                {
                    TableName = tableName,
                    Key = keys,
                    ExpressionAttributeValues = new Dictionary<string, AttributeValue>(),
                    ReturnValues = ReturnValue.UPDATED_NEW
                };

                var updateExpression = new StringBuilder();
                foreach (var field in fields.Keys)
                {
                    var valueKey = ":" + field;
                    request.ExpressionAttributeValues.Add(valueKey, fields[field]);
                    updateExpression.Append($" {field} = {valueKey},");
                }
                updateExpression.Insert(0, "SET");

                if (string.IsNullOrWhiteSpace(extraExpression))
                {
                    updateExpression.Remove(updateExpression.Length - 1, 1);
                }
                else
                {
                    updateExpression.Append($" {extraExpression}");
                    if (extraExpressionValues != null && extraExpressionValues.Count > 0)
                    {
                        foreach (var key in extraExpressionValues.Keys)
                        {
                            request.ExpressionAttributeValues.Add(key, extraExpressionValues[key]);
                        }
                    }
                }

                request.UpdateExpression = updateExpression.ToString();

                if (!string.IsNullOrWhiteSpace(conditionExpression))
                    request.ConditionExpression = conditionExpression;

                if (conditionValues != null && conditionValues.Keys.Count > 0)
                {
                    foreach (var item in conditionValues)
                    {
                        request.ExpressionAttributeValues.Add(item.Key, item.Value);
                    }
                }

                var result = await ddbClient.UpdateItemAsync(request);

                foreach (var key in result.Attributes.Keys)
                {
                    if (fields.ContainsKey(key))
                    {
                        fields[key] = result.Attributes[key];
                    }
                    else
                    {
                        fields.Add(key, result.Attributes[key]);
                    }
                }
            }
            catch (Exception exc)
            {
                Logger.Warn(ErrorCode.StorageProviderBase,
                    $"Intermediate error upserting to the table {tableName}", exc);
                throw;
            }
        }

        /// <summary>
        /// Delete an entry from a DynamoDB table
        /// </summary>
        /// <param name="tableName">The name of the table to delete an entry</param>
        /// <param name="keys">The table entry keys for the entry to be deleted</param>
        /// <param name="conditionExpression">Optional conditional expression</param>
        /// <param name="conditionValues">Optional field/attribute values used in the conditional expression</param>
        /// <returns></returns>
        public Task DeleteEntryAsync(string tableName, Dictionary<string, AttributeValue> keys, string conditionExpression = "", Dictionary<string, AttributeValue> conditionValues = null)
        {
            if (Logger.IsVerbose2) Logger.Verbose2("Deleting table {0}  entry with key(s) {1}", tableName, Utils.DictionaryToString(keys));

            try
            {
                var request = new DeleteItemRequest
                {
                    TableName = tableName,
                    Key = keys
                };

                if (!string.IsNullOrWhiteSpace(conditionExpression))
                    request.ConditionExpression = conditionExpression;

                if (conditionValues != null && conditionValues.Keys.Count > 0)
                    request.ExpressionAttributeValues = conditionValues;

                return ddbClient.DeleteItemAsync(request);
            }
            catch (Exception exc)
            {
                Logger.Warn(ErrorCode.StorageProviderBase,
                    $"Intermediate error deleting entry from the table {tableName}.", exc);
                throw;
            }
        }

        /// <summary>
        /// Delete multiple entries from a DynamoDB table (Batch delete)
        /// </summary>
        /// <param name="tableName">The name of the table to delete entries</param>
        /// <param name="toDelete">List of key values for each entry that must be deleted in the batch</param>
        /// <returns></returns>
        public Task DeleteEntriesAsync(string tableName, IReadOnlyCollection<Dictionary<string, AttributeValue>> toDelete)
        {
            if (Logger.IsVerbose2) Logger.Verbose2("Deleting {0} table entries", tableName);

            if (toDelete == null) throw new ArgumentNullException("collection");

            if (toDelete.Count == 0)
                return TaskDone.Done;

            try
            {
                var request = new BatchWriteItemRequest();
                request.RequestItems = new Dictionary<string, List<WriteRequest>>();
                var batch = new List<WriteRequest>();

                foreach (var keys in toDelete)
                {
                    var writeRequest = new WriteRequest();
                    writeRequest.DeleteRequest = new DeleteRequest();
                    writeRequest.DeleteRequest.Key = keys;
                    batch.Add(writeRequest);
                }
                request.RequestItems.Add(tableName, batch);
                return ddbClient.BatchWriteItemAsync(request);
            }
            catch (Exception exc)
            {
                Logger.Warn(ErrorCode.StorageProviderBase,
                    $"Intermediate error deleting entries from the table {tableName}.", exc);
                throw;
            }
        }

        /// <summary>
        /// Read an entry from a DynamoDB table
        /// </summary>
        /// <typeparam name="TResult">The result type</typeparam>
        /// <param name="tableName">The name of the table to search for the entry</param>
        /// <param name="keys">The table entry keys to search for</param>
        /// <param name="resolver">Function that will be called to translate the returned fields into a concrete type. This Function is only called if the result is != null</param>
        /// <returns>The object translated by the resolver function</returns>
        public async Task<TResult> ReadSingleEntryAsync<TResult>(string tableName, Dictionary<string, AttributeValue> keys, Func<Dictionary<string, AttributeValue>, TResult> resolver) where TResult : class
        {
            try
            {
                var request = new GetItemRequest
                {
                    TableName = tableName,
                    Key = keys,
                    ConsistentRead = true
                };

                var response = await ddbClient.GetItemAsync(request);

                if (response.IsItemSet)
                {
                    return resolver(response.Item);
                }
                else
                {
                    return null;
                }
            }
            catch (Exception)
            {
                if (Logger.IsVerbose) Logger.Verbose("Unable to find table entry for Keys = {0}", Utils.DictionaryToString(keys));
                throw;
            }
        }

        /// <summary>
        /// Query for multiple entries in a DynamoDB table by filtering its keys
        /// </summary>
        /// <typeparam name="TResult">The result type</typeparam>
        /// <param name="tableName">The name of the table to search for the entries</param>
        /// <param name="keys">The table entry keys to search for</param>
        /// <param name="keyConditionExpression">the expression that will filter the keys</param>
        /// <param name="resolver">Function that will be called to translate the returned fields into a concrete type. This Function is only called if the result is != null and will be called for each entry that match the query and added to the results list</param>
        /// <param name="indexName">In case a secondary index is used in the keyConditionExpression</param>
        /// <param name="scanIndexForward">In case an index is used, show if the seek order is ascending (true) or descending (false)</param>
        /// <returns>The collection containing a list of objects translated by the resolver function</returns>
        public async Task<List<TResult>> QueryAsync<TResult>(string tableName, Dictionary<string, AttributeValue> keys, string keyConditionExpression, Func<Dictionary<string, AttributeValue>, TResult> resolver, string indexName = "", bool scanIndexForward = true) where TResult : class
        {
            try
            {
                var request = new QueryRequest
                {
                    TableName = tableName,
                    ExpressionAttributeValues = keys,
                    ConsistentRead = true,
                    KeyConditionExpression = keyConditionExpression,
                    Select = Select.ALL_ATTRIBUTES
                };

                if (!string.IsNullOrWhiteSpace(indexName))
                {
                    request.ScanIndexForward = scanIndexForward;
                    request.IndexName = indexName;
                }

                var response = await ddbClient.QueryAsync(request);

                var resultList = new List<TResult>();
                foreach (var item in response.Items)
                {
                    resultList.Add(resolver(item));
                }
                return resultList;
            }
            catch (Exception)
            {
                if (Logger.IsVerbose) Logger.Verbose("Unable to find table entry for Keys = {0}", Utils.DictionaryToString(keys));
                throw;
            }
        }

        /// <summary>
        /// Scan a DynamoDB table by querying the entry fields.
        /// </summary>
        /// <typeparam name="TResult">The result type</typeparam>
        /// <param name="tableName">The name of the table to search for the entries</param>
        /// <param name="attributes">The attributes used on the expression</param>
        /// <param name="expression">The filter expression</param>
        /// <param name="resolver">Function that will be called to translate the returned fields into a concrete type. This Function is only called if the result is != null and will be called for each entry that match the query and added to the results list</param>
        /// <returns>The collection containing a list of objects translated by the resolver function</returns>
        public async Task<List<TResult>> ScanAsync<TResult>(string tableName, Dictionary<string, AttributeValue> attributes, string expression, Func<Dictionary<string, AttributeValue>, TResult> resolver) where TResult : class
        {
            try
            {
                var request = new ScanRequest
                {
                    TableName = tableName,
                    ConsistentRead = true,
                    FilterExpression = expression,
                    ExpressionAttributeValues = attributes,
                    Select = Select.ALL_ATTRIBUTES
                };

                var response = await ddbClient.ScanAsync(request);

                var resultList = new List<TResult>();
                foreach (var item in response.Items)
                {
                    resultList.Add(resolver(item));
                }
                return resultList;
            }
            catch (Exception exc)
            {
                var errorMsg = $"Failed to read table {tableName}: {exc.Message}";
                Logger.Warn(ErrorCode.StorageProviderBase, errorMsg, exc);
                throw new OrleansException(errorMsg, exc);
            }
        }

        /// <summary>
        /// Crete or replace multiple entries in a DynamoDB table (Batch put)
        /// </summary>
        /// <param name="tableName">The name of the table to search for the entry</param>
        /// <param name="toCreate">List of key values for each entry that must be created or replaced in the batch</param>
        /// <returns></returns>
        public Task PutEntriesAsync(string tableName, IReadOnlyCollection<Dictionary<string, AttributeValue>> toCreate)
        {
            if (Logger.IsVerbose2) Logger.Verbose2("Put entries {0} table", tableName);

            if (toCreate == null) throw new ArgumentNullException("collection");

            if (toCreate.Count == 0)
                return TaskDone.Done;

            try
            {
                var request = new BatchWriteItemRequest();
                request.RequestItems = new Dictionary<string, List<WriteRequest>>();
                var batch = new List<WriteRequest>();

                foreach (var item in toCreate)
                {
                    var writeRequest = new WriteRequest();
                    writeRequest.PutRequest = new PutRequest();
                    writeRequest.PutRequest.Item = item;
                    batch.Add(writeRequest);
                }
                request.RequestItems.Add(tableName, batch);
                return ddbClient.BatchWriteItemAsync(request);
            }
            catch (Exception exc)
            {
                Logger.Warn(ErrorCode.StorageProviderBase,
                    $"Intermediate error bulk inserting entries to table {tableName}.", exc);
                throw;
            }
        }

        #endregion
    }
}
