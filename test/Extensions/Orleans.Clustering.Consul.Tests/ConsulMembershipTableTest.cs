using System.Collections.Immutable;
using System.Net;
using System.Text;
using Consul;
using Newtonsoft.Json;
using Orleans.Messaging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Runtime.Host;
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
        {
            ConsulTestUtils.EnsureConsul();
            var options = new ConsulClusteringOptions();
            var address = new Uri(this.connectionString);

            options.ConfigureConsulClient(address);

            return new ConsulBasedMembershipTable(loggerFactory.CreateLogger<ConsulBasedMembershipTable>(), Options.Create(options), this._clusterOptions);
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

            return new ConsulGatewayListProvider(loggerFactory.CreateLogger<ConsulGatewayListProvider>(), Options.Create(options), this._gatewayOptions, this._clusterOptions);
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
            await MembershipTable_InsertRow(false);
        }

        [Fact, TestCategory("Functional")]
        public async Task MembershipTable_Consul_MetadataRoundTrips()
        {
            await MembershipTable_MetadataRoundTrips();
        }

        [Fact, TestCategory("Functional")]
        public async Task MembershipTable_Consul_ReadRow_Insert_Read()
        {
            await MembershipTable_ReadRow_Insert_Read(false);
        }

        [Fact, TestCategory("Functional")]
        public async Task MembershipTable_Consul_ReadAll_Insert_ReadAll()
        {
            await MembershipTable_ReadAll_Insert_ReadAll(false);
        }

        [Fact, TestCategory("Functional")]
        public async Task MembershipTable_Consul_UpdateRow()
        {
            await MembershipTable_UpdateRow(false);
        }

        [Fact, TestCategory("Functional")]
        public async Task MembershipTable_Consul_UpdateRowInParallel()
        {
            await MembershipTable_UpdateRowInParallel(false);
        }

        /// <summary>
        /// Tests the "I Am Alive" heartbeat mechanism.
        /// Verifies that silos can update their liveness timestamp
        /// in Consul to indicate they are still running.
        /// </summary>
        [Fact, TestCategory("Functional")]
        public async Task MembershipTable_Consul_UpdateIAmAlive()
        {
            await MembershipTable_UpdateIAmAlive(false);
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
            await MembershipTable_CleanupDefunctSiloEntries(false);
        }

        [Fact, TestCategory("Functional")]
        public async Task MembershipMetadata_HeartbeatRepairsMissingInlineMetadata()
        {
            var options = new ConsulClusteringOptions();
            options.ConfigureConsulClient(new Uri(connectionString));
            var membership = new ConsulBasedMembershipTable(
                loggerFactory.CreateLogger<ConsulBasedMembershipTable>(),
                Options.Create(options),
                _clusterOptions);

            var entry = CreateMetadataEntry();
            var initial = await membership.ReadAll();
            Assert.True(await membership.InsertRow(entry, initial.Version.Next()));

            using var client = options.CreateClient();
            var address = entry.SiloAddress.ToParsableString();
            var membershipKey = $"orleans/{clusterId}/{address}";
            var legacyPair = (await client.KV.Get(membershipKey)).Response;
            var legacyRegistration = JsonConvert.DeserializeObject<ConsulSiloRegistration>(
                Encoding.UTF8.GetString(legacyPair.Value))!;
            legacyRegistration.Metadata = null;
            legacyPair.Value = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(legacyRegistration));
            Assert.True((await client.KV.Put(legacyPair)).Response);

            var versionBeforeHeartbeat = (await membership.ReadAll()).Version;
            entry.IAmAliveTime = entry.IAmAliveTime.AddMinutes(1);
            await membership.UpdateIAmAlive(entry);

            var repairedPair = (await client.KV.Get(membershipKey)).Response;
            var repairedRegistration = JsonConvert.DeserializeObject<ConsulSiloRegistration>(
                Encoding.UTF8.GetString(repairedPair.Value))!;
            Assert.Equal(entry.Metadata, repairedRegistration.Metadata);
            Assert.Equal(versionBeforeHeartbeat, (await membership.ReadAll()).Version);
        }

        [Fact, TestCategory("Functional")]
        public async Task MembershipMetadata_HeartbeatDoesNotOverwritePresentInlineMetadata()
        {
            var options = new ConsulClusteringOptions();
            options.ConfigureConsulClient(new Uri(connectionString));
            var membership = new ConsulBasedMembershipTable(
                loggerFactory.CreateLogger<ConsulBasedMembershipTable>(),
                Options.Create(options),
                _clusterOptions);
            var entry = CreateMetadataEntry();
            var initial = await membership.ReadAll();
            Assert.True(await membership.InsertRow(entry, initial.Version.Next()));
            var expected = entry.Metadata;

            using var client = options.CreateClient();
            var membershipKey = $"orleans/{clusterId}/{entry.SiloAddress.ToParsableString()}";
            var originalIndex = (await client.KV.Get(membershipKey)).Response.ModifyIndex;

            entry.Metadata = ImmutableDictionary<string, string>.Empty.Add("region", "conflict");
            entry.IAmAliveTime = entry.IAmAliveTime.AddMinutes(1);
            await membership.UpdateIAmAlive(entry);

            var storedPair = (await client.KV.Get(membershipKey)).Response;
            var storedRegistration = JsonConvert.DeserializeObject<ConsulSiloRegistration>(
                Encoding.UTF8.GetString(storedPair.Value))!;
            Assert.Equal(expected, storedRegistration.Metadata);
            Assert.Equal(originalIndex, storedPair.ModifyIndex);
        }

        private static MembershipEntry CreateMetadataEntry() => new()
        {
            SiloAddress = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 12345), 123456),
            HostName = "host",
            SiloName = "silo",
            Status = SiloStatus.Joining,
            StartTime = DateTime.UtcNow,
            IAmAliveTime = DateTime.UtcNow,
            Metadata = ImmutableDictionary<string, string>.Empty.Add("region", "west")
        };
    }
}
