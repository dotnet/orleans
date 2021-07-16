using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Amazon.Runtime.CredentialManagement;

#if CLUSTERING_DYNAMODB
namespace Orleans.Clustering.DynamoDB
#elif PERSISTENCE_DYNAMODB
namespace Orleans.Persistence.DynamoDB
#elif REMINDERS_DYNAMODB
namespace Orleans.Reminders.DynamoDB
#elif AWSUTILS_TESTS
namespace Orleans.AWSUtils.Tests
#elif TRANSACTIONS_DYNAMODB
namespace Orleans.Transactions.DynamoDB
#else
// No default namespace intentionally to cause compile errors if something is not defined
#endif
{
    /// <summary>
    /// Wrapper around AWS DynamoDB SDK.
    /// </summary>
    internal class DynamoDBStorage
    {
        private string accessKey;
        private string token;
        private string profileName;
        /// <summary> Secret key for this dynamoDB table </summary>
        protected string secretKey;
        private string service;
        public const int DefaultReadCapacityUnits = 10;
        public const int DefaultWriteCapacityUnits = 5;
        private int readCapacityUnits = DefaultReadCapacityUnits;
        private int writeCapacityUnits = DefaultWriteCapacityUnits;
        private readonly bool useProvisionedThroughput;
        private AmazonDynamoDBClient ddbClient;
        private ILogger Logger;

        /// <summary>
        /// Create a DynamoDBStorage instance
        /// </summary>
        /// <param name="logger"></param>
        /// <param name="accessKey"></param>
        /// <param name="secretKey"></param>
        /// <param name="token"></param>
        /// <param name="profileName"></param>
        /// <param name="service"></param>
        /// <param name="readCapacityUnits"></param>
        /// <param name="writeCapacityUnits"></param>
        /// <param name="useProvisionedThroughput"></param>
        public DynamoDBStorage(
            ILogger logger,
            string service,
            string accessKey = "",
            string secretKey = "",
            string token = "",
            string profileName = "",
            int readCapacityUnits = DefaultReadCapacityUnits,
            int writeCapacityUnits = DefaultWriteCapacityUnits,
            bool useProvisionedThroughput = true)
        {
            if (service == null) throw new ArgumentNullException(nameof(service));
            this.accessKey = accessKey;
            this.secretKey = secretKey;
            this.token = token;
            this.profileName = profileName;
            this.service = service;
            this.readCapacityUnits = readCapacityUnits;
            this.writeCapacityUnits = writeCapacityUnits;
            this.useProvisionedThroughput = useProvisionedThroughput;
            Logger = logger;
            CreateClient();
        }

        /// <summary>
        /// Create a DynamoDB table if it doesn't exist
        /// </summary>
        /// <param name="tableName">The name of the table</param>
        /// <param name="keys">The keys definitions</param>
        /// <param name="attributes">The attributes used on the key definition</param>
        /// <param name="secondaryIndexes">(optional) The secondary index definitions</param>
        /// <param name="ttlAttributeName">(optional) The name of the item attribute that indicates the item TTL (if null, ttl won't be enabled)</param>
        /// <returns></returns>
        public async Task InitializeTable(string tableName, List<KeySchemaElement> keys, List<AttributeDefinition> attributes, List<GlobalSecondaryIndex> secondaryIndexes = null, string ttlAttributeName = null)
        {
            try
            {
                if (await GetTableDescription(tableName) == null)
                    await CreateTable(tableName, keys, attributes, secondaryIndexes, ttlAttributeName);
            }
            catch (Exception exc)
            {
                Logger.Error(ErrorCode.StorageProviderBase, $"Could not initialize connection to storage table {tableName}", exc);
                throw;
            }
        }

        private void CreateClient()
        {
            if (this.service.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                this.service.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                // Local DynamoDB instance (for testing)
                var credentials = new BasicAWSCredentials("dummy", "dummyKey");
                this.ddbClient = new AmazonDynamoDBClient(credentials, new AmazonDynamoDBConfig { ServiceURL = this.service });
            }
            else if (!string.IsNullOrEmpty(this.accessKey) && !string.IsNullOrEmpty(this.secretKey) && !string.IsNullOrEmpty(this.token))
            {
                // AWS DynamoDB instance (auth via explicit credentials and token)
                var credentials = new SessionAWSCredentials(this.accessKey, this.secretKey, this.token);
                this.ddbClient = new AmazonDynamoDBClient(credentials, new AmazonDynamoDBConfig {RegionEndpoint = AWSUtils.GetRegionEndpoint(this.service)});
            }
            else if (!string.IsNullOrEmpty(this.accessKey) && !string.IsNullOrEmpty(this.secretKey))
            {
                // AWS DynamoDB instance (auth via explicit credentials)
                var credentials = new BasicAWSCredentials(this.accessKey, this.secretKey);
                this.ddbClient = new AmazonDynamoDBClient(credentials, new AmazonDynamoDBConfig {RegionEndpoint = AWSUtils.GetRegionEndpoint(this.service)});
            }
            else if (!string.IsNullOrEmpty(this.profileName))
            {
                // AWS DynamoDB instance (auth via explicit credentials and token found in a named profile)
                var chain = new CredentialProfileStoreChain();
                if (chain.TryGetAWSCredentials(this.profileName, out var credentials))
                {
                    this.ddbClient = new AmazonDynamoDBClient(
                        credentials,
                        new AmazonDynamoDBConfig
                        {
                            RegionEndpoint = AWSUtils.GetRegionEndpoint(this.service)
                        });
                }
                else
                {
                    throw new InvalidOperationException(
                        $"AWS named profile '{this.profileName}' provided, but credentials could not be retrieved");
                }
            }
            else
            {
                // AWS DynamoDB instance (implicit auth - EC2 IAM Roles etc)
                this.ddbClient = new AmazonDynamoDBClient(new AmazonDynamoDBConfig {RegionEndpoint = AWSUtils.GetRegionEndpoint(this.service)});
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

        private async Task CreateTable(string tableName, List<KeySchemaElement> keys, List<AttributeDefinition> attributes, List<GlobalSecondaryIndex> secondaryIndexes = null, string ttlAttributeName = null)
        {
            var request = new CreateTableRequest
            {
                TableName = tableName,
                AttributeDefinitions = attributes,
                KeySchema = keys,
                BillingMode = this.useProvisionedThroughput ? BillingMode.PROVISIONED : BillingMode.PAY_PER_REQUEST,
                ProvisionedThroughput = this.useProvisionedThroughput ? new ProvisionedThroughput
                {
                    ReadCapacityUnits = readCapacityUnits,
                    WriteCapacityUnits = writeCapacityUnits
                } : null
            };

            if (secondaryIndexes != null && secondaryIndexes.Count > 0)
            {
                if (this.useProvisionedThroughput)
                {
                    var indexThroughput = new ProvisionedThroughput {ReadCapacityUnits = readCapacityUnits, WriteCapacityUnits = writeCapacityUnits};
                    secondaryIndexes.ForEach(i =>
                    {
                        i.ProvisionedThroughput = indexThroughput;
                    });
                }

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

                if (!string.IsNullOrEmpty(ttlAttributeName))
                {
                    await ddbClient.UpdateTimeToLiveAsync(new UpdateTimeToLiveRequest
                    {
                        TableName = tableName,
                        TimeToLiveSpecification = new TimeToLiveSpecification { AttributeName = ttlAttributeName, Enabled = true }
                    });
                }
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
            if (Logger.IsEnabled(LogLevel.Trace)) Logger.Trace("Creating {0} table entry: {1}", tableName, Utils.DictionaryToString(fields));

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
            if (Logger.IsEnabled(LogLevel.Trace)) Logger.Trace("Upserting entry {0} with key(s) {1} into table {2}", Utils.DictionaryToString(fields), Utils.DictionaryToString(keys), tableName);

            try
            {
                var request = new UpdateItemRequest
                {
                    TableName = tableName,
                    Key = keys,
                    ReturnValues = ReturnValue.UPDATED_NEW
                };

                (request.UpdateExpression, request.ExpressionAttributeValues) = ConvertUpdate(fields, conditionValues,
                    extraExpression, extraExpressionValues);

                if (!string.IsNullOrWhiteSpace(conditionExpression))
                    request.ConditionExpression = conditionExpression;

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

        public (string updateExpression, Dictionary<string, AttributeValue> expressionAttributeValues)
            ConvertUpdate(Dictionary<string, AttributeValue> fields,
                Dictionary<string, AttributeValue> conditionValues = null,
                string extraExpression = "", Dictionary<string, AttributeValue> extraExpressionValues = null)
        {
            var expressionAttributeValues = new Dictionary<string, AttributeValue>();

            var updateExpression = new StringBuilder();
            foreach (var field in fields.Keys)
            {
                var valueKey = ":" + field;
                expressionAttributeValues.Add(valueKey, fields[field]);
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
                        expressionAttributeValues.Add(key, extraExpressionValues[key]);
                    }
                }
            }

            if (conditionValues != null && conditionValues.Keys.Count > 0)
            {
                foreach (var item in conditionValues)
                {
                    expressionAttributeValues.Add(item.Key, item.Value);
                }
            }

            return (updateExpression.ToString(), expressionAttributeValues);
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
            if (Logger.IsEnabled(LogLevel.Trace)) Logger.Trace("Deleting table {0}  entry with key(s) {1}", tableName, Utils.DictionaryToString(keys));

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
            if (Logger.IsEnabled(LogLevel.Trace)) Logger.Trace("Deleting {0} table entries", tableName);

            if (toDelete == null) throw new ArgumentNullException("collection");

            if (toDelete.Count == 0)
                return Task.CompletedTask;

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
                if (Logger.IsEnabled(LogLevel.Debug)) Logger.Debug("Unable to find table entry for Keys = {0}", Utils.DictionaryToString(keys));
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
        /// <param name="lastEvaluatedKey">The primary key of the first item that this operation will evaluate. Use the value that was returned for LastEvaluatedKey in the previous operation</param>
        /// <returns>The collection containing a list of objects translated by the resolver function and the LastEvaluatedKey for paged results</returns>
        public async Task<(List<TResult> results, Dictionary<string, AttributeValue> lastEvaluatedKey)> QueryAsync<TResult>(string tableName, Dictionary<string, AttributeValue> keys, string keyConditionExpression, Func<Dictionary<string, AttributeValue>, TResult> resolver, string indexName = "", bool scanIndexForward = true, Dictionary<string, AttributeValue> lastEvaluatedKey = null) where TResult : class
        {
            try
            {
                var request = new QueryRequest
                {
                    TableName = tableName,
                    ExpressionAttributeValues = keys,
                    ConsistentRead = true,
                    KeyConditionExpression = keyConditionExpression,
                    Select = Select.ALL_ATTRIBUTES,
                    ExclusiveStartKey = lastEvaluatedKey
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
                return (resultList, response.LastEvaluatedKey);
            }
            catch (Exception)
            {
                if (Logger.IsEnabled(LogLevel.Debug)) Logger.Debug("Unable to find table entry for Keys = {0}", Utils.DictionaryToString(keys));
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
        public async Task<List<TResult>> QueryAllAsync<TResult>(string tableName, Dictionary<string, AttributeValue> keys,
                string keyConditionExpression, Func<Dictionary<string, AttributeValue>, TResult> resolver,
                string indexName = "", bool scanIndexForward = true) where TResult : class
        {
            List<TResult> resultList = null;
            Dictionary<string, AttributeValue> lastEvaluatedKey = null;
            do
            {
                List<TResult> results;
                (results, lastEvaluatedKey) = await QueryAsync(tableName, keys, keyConditionExpression, resolver,
                    indexName, scanIndexForward, lastEvaluatedKey);
                if (resultList == null)
                {
                    resultList = results;
                }
                else
                {
                    resultList.AddRange(results);
                }
            } while (lastEvaluatedKey.Count != 0);

            return resultList;
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
            // From the Amazon documentation:
            // "A single Scan operation will read up to the maximum number of items set
            // (if using the Limit parameter) or a maximum of 1 MB of data and then apply
            // any filtering to the results using FilterExpression."
            // https://docs.aws.amazon.com/sdkfornet/v3/apidocs/items/DynamoDBv2/MDynamoDBScanAsyncStringDictionary!String,%20Condition!CancellationToken.html

            try
            {
                var resultList = new List<TResult>();

                var exclusiveStartKey = new Dictionary<string, AttributeValue>();

                while (true)
                {
                    var request = new ScanRequest
                    {
                        TableName = tableName,
                        ConsistentRead = true,
                        FilterExpression = expression,
                        ExpressionAttributeValues = attributes,
                        Select = Select.ALL_ATTRIBUTES,
                        ExclusiveStartKey = exclusiveStartKey
                    };

                    var response = await ddbClient.ScanAsync(request);

                    foreach (var item in response.Items)
                    {
                        resultList.Add(resolver(item));
                    }

                    if (response.LastEvaluatedKey.Count == 0)
                    {
                        break;
                    }
                    else
                    {
                        exclusiveStartKey = response.LastEvaluatedKey;
                    }
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
            if (Logger.IsEnabled(LogLevel.Trace)) Logger.Trace("Put entries {0} table", tableName);

            if (toCreate == null) throw new ArgumentNullException("collection");

            if (toCreate.Count == 0)
                return Task.CompletedTask;

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

        /// <summary>
        /// Transactionally reads entries from a DynamoDB table
        /// </summary>
        /// <typeparam name="TResult">The result type</typeparam>
        /// <param name="tableName">The name of the table to search for the entry</param>
        /// <param name="keys">The table entry keys to search for</param>
        /// <param name="resolver">Function that will be called to translate the returned fields into a concrete type. This Function is only called if the result is != null</param>
        /// <returns>The object translated by the resolver function</returns>
        public async Task<IEnumerable<TResult>> GetEntriesTxAsync<TResult>(string tableName, IEnumerable<Dictionary<string, AttributeValue>> keys, Func<Dictionary<string, AttributeValue>, TResult> resolver) where TResult : class
        {
            try
            {
                var request = new TransactGetItemsRequest
                {
                    TransactItems = keys.Select(key => new TransactGetItem
                    {
                        Get = new Get
                        {
                            TableName = tableName,
                            Key = key
                        }
                    }).ToList()
                };

                var response = await ddbClient.TransactGetItemsAsync(request);

                return response.Responses.Where(r => r?.Item?.Count > 0).Select(r => resolver(r.Item));
            }
            catch (Exception)
            {
                if (Logger.IsEnabled(LogLevel.Debug)) Logger.Debug("Unable to find table entry for Keys = {0}", Utils.EnumerableToString(keys, d => Utils.DictionaryToString(d)));
                throw;
            }
        }

        /// <summary>
        /// Transactionally performs write requests
        /// </summary>
        /// <param name="puts">Any puts to be performed</param>
        /// <param name="updates">Any updated to be performed</param>
        /// <param name="deletes">Any deletes to be performed</param>
        /// <param name="conditionChecks">Any condition checks to be performed</param>
        /// <returns></returns>
        public Task WriteTxAsync(IEnumerable<Put> puts = null, IEnumerable<Update> updates = null, IEnumerable<Delete> deletes = null, IEnumerable<ConditionCheck> conditionChecks = null)
        {
            try
            {
                var transactItems = new List<TransactWriteItem>();
                if (puts != null)
                {
                    transactItems.AddRange(puts.Select(p => new TransactWriteItem{Put = p}));
                }
                if (updates != null)
                {
                    transactItems.AddRange(updates.Select(u => new TransactWriteItem{Update = u}));
                }
                if (deletes != null)
                {
                    transactItems.AddRange(deletes.Select(d => new TransactWriteItem{Delete = d}));
                }
                if (conditionChecks != null)
                {
                    transactItems.AddRange(conditionChecks.Select(c => new TransactWriteItem{ConditionCheck = c}));
                }

                var request = new TransactWriteItemsRequest
                {
                    TransactItems = transactItems
                };

                return ddbClient.TransactWriteItemsAsync(request);
            }
            catch (Exception)
            {
                if (Logger.IsEnabled(LogLevel.Debug)) Logger.Debug("Unable to write tx");
                throw;
            }
        }
    }
}
