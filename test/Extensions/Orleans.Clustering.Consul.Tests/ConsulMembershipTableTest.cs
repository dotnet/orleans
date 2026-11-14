using System.Net;
using Consul;
using Orleans.Messaging;
using Orleans.Clustering.TestKit;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Runtime.Membership;
using TestExtensions;
using UnitTests;
using UnitTests.MembershipTests;
using Xunit;

namespace Consul.Tests
{
    /// <summary>
    /// Tests for operation of Orleans Membership Table using Consul - Requires access to external Consul cluster
    /// 
    /// Consul provides a distributed key-value store that Orleans uses for:
    /// - Cluster membership management
    /// - Service discovery
    /// - Health checking and failure detection
    /// 
    /// These tests verify that the Consul-based membership provider correctly implements
    /// all required membership table operations including:
    /// - Reading and writing silo entries
    /// - Updating liveness information (I Am Alive)
    /// - Gateway discovery for clients
    /// - Cleanup of defunct silo entries
    /// </summary>
    [TestCategory("Membership"), TestCategory("Consul")]
    [TestSuite("Functional")]
    [TestProvider("Consul")]
    [TestArea("Membership")]
    public class ConsulMembershipTableTest : MembershipTableTestsBase
    {
        private IConsulClient _legacyGatewayClient = null!;

        public ConsulMembershipTableTest(ConnectionStringFixture fixture, TestEnvironmentFixture environment) : base(fixture, environment, CreateFilters())
        {
        }

        private static LoggerFilterOptions CreateFilters()
        {
            var filters = new LoggerFilterOptions();
            filters.AddFilter("ConsulBasedMembershipTable", Microsoft.Extensions.Logging.LogLevel.Trace);
            filters.AddFilter("Storage", Microsoft.Extensions.Logging.LogLevel.Trace);
            return filters;
        }

        /// <summary>
        /// Creates a Consul-based membership table for testing.
        /// Configures the Consul client with the test server address
        /// and creates the membership table implementation.
        /// </summary>
        protected override IMembershipTable CreateMembershipTable(ILogger logger)
            => CreateMembershipTable(logger, _clusterOptions);

        protected override IMembershipTable CreateMembershipTable(ILogger logger, IOptions<ClusterOptions> clusterOptions)
        {
            ConsulTestUtils.EnsureConsul();
            var options = new ConsulClusteringOptions();
            var address = new Uri(this.connectionString);

            options.ConfigureConsulClient(address);

            return new ConsulBasedMembershipTable(loggerFactory.CreateLogger<ConsulBasedMembershipTable>(), Options.Create(options), clusterOptions);
        }

        protected override MembershipTableTestHandle CreateConformanceHandle(ILogger logger, IOptions<ClusterOptions> clusterOptions)
        {
            var options = new ConsulClusteringOptions();
            options.ConfigureConsulClient(new Uri(connectionString));
            var client = options.CreateClient();
            options.ConfigureConsulClient(() => client);
            var table = new ConsulBasedMembershipTable(
                loggerFactory.CreateLogger<ConsulBasedMembershipTable>(), Options.Create(options), clusterOptions);
            return new MembershipTableTestHandle(table, () =>
            {
                client.Dispose();
                return ValueTask.CompletedTask;
            });
        }

        protected override MembershipTableTestHandle CreateLegacyMembershipTableHandle(ILogger logger)
            => CreateConformanceHandle(logger, _clusterOptions);

        protected override MembershipTableTestFixture CreateConformanceFixture()
            => CreateConformanceFixture(IsConformanceClusterDeletedAsync);

        private async ValueTask<bool> IsConformanceClusterDeletedAsync(string clusterId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var client = new ConsulClient(options => options.Address = new Uri(connectionString));
            var response = await client.KV.Keys(
                $"orleans/{clusterId}/",
                separator: null,
                new QueryOptions { Consistency = ConsistencyMode.Consistent },
                cancellationToken);
            if (response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.NotFound))
            {
                throw new InvalidOperationException($"Consul deletion probe returned {response.StatusCode}.");
            }

            return response.Response is null or { Length: 0 };
        }

        /// <summary>
        /// Creates a Consul-based gateway list provider for testing.
        /// This provider allows clients to discover available gateways
        /// by querying the Consul service registry.
        /// </summary>
        protected override IGatewayListProvider CreateGatewayListProvider(ILogger logger)
        {
            ConsulTestUtils.EnsureConsul();
            var options = new ConsulClusteringOptions();
            var address = new Uri(this.connectionString);

            options.ConfigureConsulClient(address);
            var client = options.CreateClient();
            _legacyGatewayClient = client;
            options.ConfigureConsulClient(() => client);

            return new ConsulGatewayListProvider(loggerFactory.CreateLogger<ConsulGatewayListProvider>(), Options.Create(options), this._gatewayOptions, this._clusterOptions);
        }

        protected override ValueTask DisposeLegacyGatewayListProviderAsync(IGatewayListProvider gatewayListProvider)
        {
            _legacyGatewayClient.Dispose();
            return ValueTask.CompletedTask;
        }

        protected override async Task<string> GetConnectionString()
        {
            return await ConsulTestUtils.EnsureConsulAsync(TestContext.Current.CancellationToken)
                ? ConsulTestUtils.ConsulConnectionString
                : null!;
        }

        /// <summary>
        /// Tests gateway discovery through Consul.
        /// Verifies that clients can retrieve the list of available
        /// gateway silos from the Consul service registry.
        /// </summary>
        [Fact, TestCategory("Functional")]
        public async Task MembershipTable_Consul_GetGateways()
        {
            await MembershipTable_GetGateways();
        }

        /// <summary>
        /// Tests reading from an empty membership table.
        /// Verifies that the provider correctly handles the case
        /// when no silos have registered yet.
        /// </summary>
        [Fact, TestCategory("Functional")]
        public async Task MembershipTable_Consul_ReadAll_EmptyTable()
        {
            await MembershipTable_ReadAll_EmptyTable();
        }

        [Fact, TestCategory("Functional")]
        public async Task MembershipTable_Consul_InsertRow()
        {
            await MembershipTable_InsertRow();
        }

        [Fact, TestCategory("Functional")]
        public async Task MembershipTable_Consul_ReadRow_Insert_Read()
        {
            await MembershipTable_ReadRow_Insert_Read();
        }

        [Fact, TestCategory("Functional")]
        public async Task MembershipTable_Consul_ReadAll_Insert_ReadAll()
        {
            await MembershipTable_ReadAll_Insert_ReadAll();
        }

        [Fact, TestCategory("Functional")]
        public async Task MembershipTable_Consul_UpdateRow()
        {
            await MembershipTable_UpdateRow();
        }

        [Fact, TestCategory("Functional")]
        public async Task MembershipTable_Consul_UpdateRowInParallel()
        {
            await MembershipTable_UpdateRowInParallel();
        }

        /// <summary>
        /// Tests the "I Am Alive" heartbeat mechanism.
        /// Verifies that silos can update their liveness timestamp
        /// in Consul to indicate they are still running.
        /// </summary>
        [Fact, TestCategory("Functional")]
        public async Task MembershipTable_Consul_UpdateIAmAlive()
        {
            await MembershipTable_UpdateIAmAlive();
        }

        /// <summary>
        /// Tests cleanup of dead silo entries.
        /// Verifies that the membership table can remove entries
        /// for silos that have been declared dead to prevent
        /// the table from growing indefinitely.
        /// </summary>
        [Fact, TestCategory("Functional")]
        public async Task MembershipTable_Consul_CleanupDefunctSiloEntries()
        {
            await MembershipTable_CleanupDefunctSiloEntries();
        }
    }
}
