using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Microsoft.Extensions.Logging;
using Orleans.Configuration;
using Orleans.Streams;

namespace Orleans.Streaming.Kinesis
{
    internal interface IDynamoDBStreamCheckpointStore : IStreamCheckpointStore;

    internal sealed partial class DynamoDBStreamCheckpointStore : IDynamoDBStreamCheckpointStore
    {
        internal const string NamespaceAttribute = "CheckpointNamespace";
        internal const string PartitionAttribute = "Partition";
        internal const string CheckpointAttribute = "Checkpoint";
        internal const string VersionAttribute = "Version";

        private static readonly TimeSpan TableStatusPollInterval = TimeSpan.FromSeconds(1);

        private readonly IAmazonDynamoDB _client;
        private readonly string _tableName;
        private readonly Dictionary<string, AttributeValue> _key;
        private readonly SemaphoreSlim _mutex = new(1, 1);

        private string _checkpoint = string.Empty;
        private long _version;
        private bool _loaded;

        public DynamoDBStreamCheckpointStore(
            IAmazonDynamoDB client,
            string tableName,
            string serviceId,
            string providerName,
            string partition)
        {
            _client = client;
            _tableName = tableName;
            _key = new Dictionary<string, AttributeValue>
            {
                [NamespaceAttribute] = new(FormatNamespace(serviceId, providerName)),
                [PartitionAttribute] = new(partition),
            };
        }

        public async ValueTask<StreamCheckpointStoreState> Load(CancellationToken cancellationToken)
        {
            await _mutex.WaitAsync(cancellationToken);
            try
            {
                await LoadCore(cancellationToken);
                return GetState();
            }
            finally
            {
                _mutex.Release();
            }
        }

        public async ValueTask<StreamCheckpointStoreState> Update(
            string checkpoint,
            string expectedVersion,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(checkpoint);
            ArgumentNullException.ThrowIfNull(expectedVersion);

            await _mutex.WaitAsync(cancellationToken);
            try
            {
                if (!_loaded)
                {
                    await LoadCore(cancellationToken);
                }

                var currentVersion = _version == 0
                    ? string.Empty
                    : _version.ToString(CultureInfo.InvariantCulture);
                if (!string.Equals(currentVersion, expectedVersion, StringComparison.Ordinal))
                {
                    return GetState();
                }

                try
                {
                    var nextVersion = checked(_version + 1);
                    var item = new Dictionary<string, AttributeValue>(_key)
                    {
                        [CheckpointAttribute] = new(checkpoint),
                        [VersionAttribute] = new() { N = nextVersion.ToString(CultureInfo.InvariantCulture) },
                    };
                    var request = new PutItemRequest
                    {
                        TableName = _tableName,
                        Item = item,
                        ConditionExpression = _version == 0
                            ? "attribute_not_exists(#namespace) AND attribute_not_exists(#partition)"
                            : "#version = :expectedVersion",
                        ExpressionAttributeNames = _version == 0
                            ? new Dictionary<string, string>
                            {
                                ["#namespace"] = NamespaceAttribute,
                                ["#partition"] = PartitionAttribute,
                            }
                            : new Dictionary<string, string>
                            {
                                ["#version"] = VersionAttribute,
                            },
                        ExpressionAttributeValues = _version == 0
                            ? null
                            : new Dictionary<string, AttributeValue>
                            {
                                [":expectedVersion"] = new()
                                {
                                    N = _version.ToString(CultureInfo.InvariantCulture),
                                },
                            },
                    };

                    _ = await _client.PutItemAsync(request, cancellationToken);
                    _checkpoint = checkpoint;
                    _version = nextVersion;
                }
                catch (ConditionalCheckFailedException)
                {
                    await LoadCore(cancellationToken);
                }

                return GetState();
            }

            finally
            {
                _mutex.Release();
            }
        }

        private StreamCheckpointStoreState GetState()
            => new(
                _checkpoint,
                _version == 0 ? string.Empty : _version.ToString(CultureInfo.InvariantCulture));

        internal static async Task InitializeTable(
            IAmazonDynamoDB client,
            DynamoDBStreamQueueCheckpointerOptions options,
            ILogger logger,
            CancellationToken cancellationToken = default)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(options.InitializationTimeout);
            try
            {
                TableDescription? table = null;
                try
                {
                    table = (await client.DescribeTableAsync(options.TableName, timeout.Token)).Table;
                }
                catch (ResourceNotFoundException) when (options.CreateIfNotExists)
                {
                    var request = new CreateTableRequest
                    {
                        TableName = options.TableName,
                        AttributeDefinitions =
                        [
                            new(NamespaceAttribute, ScalarAttributeType.S),
                            new(PartitionAttribute, ScalarAttributeType.S),
                        ],
                        KeySchema =
                        [
                            new(NamespaceAttribute, KeyType.HASH),
                            new(PartitionAttribute, KeyType.RANGE),
                        ],
                        BillingMode = options.UseProvisionedThroughput
                            ? BillingMode.PROVISIONED
                            : BillingMode.PAY_PER_REQUEST,
                        ProvisionedThroughput = options.UseProvisionedThroughput
                            ? new ProvisionedThroughput(options.ReadCapacityUnits, options.WriteCapacityUnits)
                            : null,
                    };

                    try
                    {
                        table = (await client.CreateTableAsync(request, timeout.Token)).TableDescription;
                    }
                    catch (ResourceInUseException)
                    {
                        table = null;
                    }
                }
                catch (ResourceNotFoundException)
                {
                    throw new OrleansConfigurationException(
                        $"The DynamoDB checkpoint table '{options.TableName}' does not exist and " +
                        $"{nameof(DynamoDBStreamQueueCheckpointerOptions.CreateIfNotExists)} is disabled.");
                }

                while (table is null || table.TableStatus != TableStatus.ACTIVE)
                {
                    LogWaitingForTable(logger, options.TableName, table?.TableStatus);
                    await Task.Delay(TableStatusPollInterval, timeout.Token);
                    try
                    {
                        table = (await client.DescribeTableAsync(options.TableName, timeout.Token)).Table;
                    }
                    catch (ResourceNotFoundException) when (options.CreateIfNotExists)
                    {
                        table = null;
                    }
                }

                ValidateTableSchema(table, options.TableName);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw;
            }
            catch (OperationCanceledException exception)
                when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw new OrleansConfigurationException(
                    $"The DynamoDB checkpoint table '{options.TableName}' did not become active within " +
                    $"{options.InitializationTimeout}.",
                    exception);
            }
        }

        internal static string FormatNamespace(string serviceId, string providerName)
        {
            static string Encode(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
            return $"{Encode(serviceId)}:{Encode(providerName)}";
        }

        private async Task LoadCore(CancellationToken cancellationToken)
        {
            var response = await _client.GetItemAsync(
                new GetItemRequest
                {
                    TableName = _tableName,
                    Key = _key,
                    ConsistentRead = true,
                },
                cancellationToken);

            if (response.Item is not { Count: > 0 } item)
            {
                _checkpoint = string.Empty;
                _version = 0;
                _loaded = true;
                return;
            }

            if (!item.TryGetValue(CheckpointAttribute, out var checkpoint)
                || string.IsNullOrEmpty(checkpoint.S)
                || !item.TryGetValue(VersionAttribute, out var version)
                || !long.TryParse(version.N, NumberStyles.None, CultureInfo.InvariantCulture, out _version)
                || _version <= 0)
            {
                throw new InvalidOperationException(
                    $"The checkpoint row in DynamoDB table '{_tableName}' has an invalid format.");
            }

            _checkpoint = checkpoint.S;
            _loaded = true;
        }

        private static void ValidateTableSchema(TableDescription table, string tableName)
        {
            var hasExpectedKeys = table.KeySchema?.Count == 2
                && table.KeySchema.Any(
                    key => key.AttributeName == NamespaceAttribute && key.KeyType == KeyType.HASH)
                && table.KeySchema.Any(
                    key => key.AttributeName == PartitionAttribute && key.KeyType == KeyType.RANGE);
            var hasExpectedAttributes = table.AttributeDefinitions?.Any(
                    attribute => attribute.AttributeName == NamespaceAttribute
                        && attribute.AttributeType == ScalarAttributeType.S) == true
                && table.AttributeDefinitions.Any(
                    attribute => attribute.AttributeName == PartitionAttribute
                        && attribute.AttributeType == ScalarAttributeType.S);

            if (!hasExpectedKeys || !hasExpectedAttributes)
            {
                throw new OrleansConfigurationException(
                    $"The DynamoDB checkpoint table '{tableName}' does not have the expected key schema.");
            }
        }

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "Waiting for DynamoDB checkpoint table {TableName} to become active. Current status: {TableStatus}.")]
        private static partial void LogWaitingForTable(ILogger logger, string tableName, TableStatus? tableStatus);
    }
}
