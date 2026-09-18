using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;
using Amazon.Runtime.CredentialManagement;
using AWSUtils.Tests.StorageTests;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Clustering.DynamoDB;
using Orleans.Clustering.TestKit;
using Orleans.Configuration;
using Orleans.Messaging;
using Orleans.Runtime.MembershipService;
using TestExtensions;
using UnitTests;
using UnitTests.MembershipTests;
using Xunit;

namespace AWSUtils.Tests.MembershipTests
{
    /// <summary>
    /// Tests for operation of Orleans Membership Table using AWS DynamoDB - Requires access to external DynamoDB storage
    /// </summary>
    [TestCategory("Membership"), TestCategory("AWS"), TestCategory("DynamoDb")]
    [TestSuite("Functional")]
    [TestProvider("DynamoDB")]
    [TestArea("Membership")]
    public class DynamoDBMembershipTableTest : MembershipTableTestsBase, IClassFixture<DynamoDBStorageTestsFixture>
    {
        public DynamoDBMembershipTableTest(ConnectionStringFixture fixture, TestEnvironmentFixture environment) : base(fixture, environment, CreateFilters())
        {
        }

        private static LoggerFilterOptions CreateFilters()
        {
            var filters = new LoggerFilterOptions();
            filters.AddFilter("DynamoDBDataManager", LogLevel.Trace);
            filters.AddFilter("OrleansSiloInstanceManager", LogLevel.Trace);
            filters.AddFilter("Storage", LogLevel.Trace);
            return filters;
        }

        protected override IMembershipTable CreateMembershipTable(ILogger logger)
            => CreateMembershipTable(logger, _clusterOptions);

        protected override IMembershipTable CreateMembershipTable(ILogger logger, IOptions<ClusterOptions> clusterOptions)
        {
            if (!AWSTestConstants.IsDynamoDbAvailable)
                throw Xunit.Sdk.SkipException.ForSkip("Unable to connect to AWS DynamoDB simulator");
            var options = new DynamoDBClusteringOptions();
            DynamoDBMembershipHelper.ParseDataConnectionString(this.connectionString, options);
            return new DynamoDBMembershipTable(this.loggerFactory, Options.Create(options), clusterOptions);
        }

        // Persisted fields and suspect votes exceed DynamoDB's 1 MiB query page at this count.
        protected override int ConformanceConcurrencyRowCount => 4096;

        protected override MembershipTableTestFixture CreateConformanceFixture()
        {
            var options = new DynamoDBClusteringOptions();
            DynamoDBMembershipHelper.ParseDataConnectionString(connectionString, options);
            AmazonDynamoDBClient? probe = null;
            return new MembershipTableTestFixture(
                GetType().Name,
                (serviceId, clusterId, cancellationToken) =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var table = CreateMembershipTable(
                        loggerFactory.CreateLogger<DynamoDBMembershipTable>(),
                        Options.Create(new ClusterOptions { ServiceId = serviceId, ClusterId = clusterId }));
                    if (probe is null)
                    {
                        var ownedProbe = CreateDeletionProbeClient(options);
                        probe = ownedProbe;
                        return ValueTask.FromResult(new MembershipTableTestHandle(table, () =>
                        {
                            ownedProbe.Dispose();
                            return ValueTask.CompletedTask;
                        }));
                    }

                    return ValueTask.FromResult(new MembershipTableTestHandle(table));
                },
                async (clusterId, cancellationToken) =>
                {
                    var request = new QueryRequest
                    {
                        TableName = options.TableName,
                        ConsistentRead = true,
                        KeyConditionExpression = "#deployment = :cluster",
                        ExpressionAttributeNames = new()
                        {
                            ["#deployment"] = SiloInstanceRecord.DEPLOYMENT_ID_PROPERTY_NAME
                        },
                        ExpressionAttributeValues = new()
                        {
                            [":cluster"] = new AttributeValue(clusterId)
                        },
                        Select = Select.COUNT
                    };
                    var empty = true;
                    do
                    {
                        // The unfiltered partition query includes VersionRow and follows every continuation key.
                        var page = await probe!.QueryAsync(request, cancellationToken);
                        empty &= page.Count == 0;
                        request.ExclusiveStartKey = page.LastEvaluatedKey;
                    }
                    while (request.ExclusiveStartKey is { Count: > 0 });

                    return empty;
                });
        }

        private static AmazonDynamoDBClient CreateDeletionProbeClient(DynamoDBClusteringOptions options)
        {
            var isServiceUrl = Uri.TryCreate(options.Service, UriKind.Absolute, out var serviceUri)
                && (serviceUri.Scheme == Uri.UriSchemeHttp || serviceUri.Scheme == Uri.UriSchemeHttps);
            var config = isServiceUrl
                ? new AmazonDynamoDBConfig { ServiceURL = options.Service }
                : new AmazonDynamoDBConfig { RegionEndpoint = Amazon.RegionEndpoint.GetBySystemName(options.Service) };
            AWSCredentials? credentials = null;
            if (!string.IsNullOrEmpty(options.AccessKey) && !string.IsNullOrEmpty(options.SecretKey))
            {
                credentials = string.IsNullOrEmpty(options.Token)
                    ? new BasicAWSCredentials(options.AccessKey, options.SecretKey)
                    : new SessionAWSCredentials(options.AccessKey, options.SecretKey, options.Token);
            }
            else if (!string.IsNullOrEmpty(options.ProfileName))
            {
                var chain = new CredentialProfileStoreChain();
                if (!chain.TryGetAWSCredentials(options.ProfileName, out credentials))
                {
                    throw new InvalidOperationException($"AWS named profile '{options.ProfileName}' could not be retrieved.");
                }
            }

            if (credentials is null && serviceUri?.Scheme == Uri.UriSchemeHttp)
            {
                credentials = new BasicAWSCredentials("dummy", "dummyKey");
            }

            return credentials is null
                ? new AmazonDynamoDBClient(config)
                : new AmazonDynamoDBClient(credentials, config);
        }

        protected override IGatewayListProvider CreateGatewayListProvider(ILogger logger)
        {
            var options = new DynamoDBGatewayOptions();
            DynamoDBGatewayListProviderHelper.ParseDataConnectionString(this.connectionString, options);
            return new DynamoDBGatewayListProvider(this.loggerFactory.CreateLogger<DynamoDBGatewayListProvider>(), Options.Create(options), this._clusterOptions, this._gatewayOptions);
        }

        protected override Task<string> GetConnectionString()
        {
            return Task.FromResult(AWSTestConstants.IsDynamoDbAvailable ? $"Service={AWSTestConstants.DynamoDbService}" : null!);
        }

        [Fact, TestCategory("Functional")]
        public async Task MembershipTable_DynamoDB_GetGateways()
        {
            await MembershipTable_GetGateways();
        }

        [Fact, TestCategory("Functional")]
        public async Task MembershipTable_DynamoDB_ReadAll_EmptyTable()
        {
            await MembershipTable_ReadAll_EmptyTable();
        }

        [Fact, TestCategory("Functional")]
        public async Task MembershipTable_DynamoDB_InsertRow()
        {
            await MembershipTable_InsertRow();
        }

        [Fact, TestCategory("Functional")]
        public async Task MembershipTable_DynamoDB_ReadRow_Insert_Read()
        {
            await MembershipTable_ReadRow_Insert_Read();
        }

        [Fact, TestCategory("Functional")]
        public async Task MembershipTable_DynamoDB_ReadAll_Insert_ReadAll()
        {
            await MembershipTable_ReadAll_Insert_ReadAll();
        }

        [Fact, TestCategory("Functional")]
        public async Task MembershipTable_DynamoDB_UpdateRow()
        {
            await MembershipTable_UpdateRow();
        }

        [Fact, TestCategory("Functional")]
        public async Task MembershipTable_DynamoDB_CleanupDefunctSiloEntries()
        {
            await MembershipTable_CleanupDefunctSiloEntries();
        }

        [Fact]
        public async Task MembershipTable_DynamoDB_UpdateRowInParallel()
        {
            await MembershipTable_UpdateRowInParallel();
        }

        [Fact]
        public async Task MembershipTable_DynamoDB_UpdateIAmAlive()
        {
            await MembershipTable_UpdateIAmAlive();
        }
    }
}
