﻿using Orleans;
using Orleans.Messaging;
using Orleans.Runtime;
using Orleans.Runtime.Host;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Runtime.Configuration;
using TestExtensions;
using UnitTests;
using UnitTests.MembershipTests;
using Xunit;

namespace Consul.Tests
{
    /// <summary>
    /// Tests for operation of Orleans Membership Table using Consul - Requires access to external Consul cluster
    /// </summary>
    [TestCategory("Membership"), TestCategory("Consul")]
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

        protected override IMembershipTable CreateMembershipTable(Logger logger)
        {
            ConsulTestUtils.EnsureConsul();

            return new ConsulBasedMembershipTable(loggerFactory.CreateLogger<ConsulBasedMembershipTable>());
        }

        protected override IGatewayListProvider CreateGatewayListProvider(Logger logger)
        {
            ConsulTestUtils.EnsureConsul();

            return new ConsulBasedMembershipTable(loggerFactory.CreateLogger<ConsulBasedMembershipTable>());
        }

        protected override async Task<string> GetConnectionString()
        {
            return await ConsulTestUtils.EnsureConsulAsync() ? ConsulTestUtils.CONSUL_ENDPOINT : null;
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task MembershipTable_Consul_GetGateways()
        {
            await MembershipTable_GetGateways();
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task MembershipTable_Consul_ReadAll_EmptyTable()
        {
            await MembershipTable_ReadAll_EmptyTable();
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task MembershipTable_Consul_InsertRow()
        {
            await MembershipTable_InsertRow(false);
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task MembershipTable_Consul_ReadRow_Insert_Read()
        {
            await MembershipTable_ReadRow_Insert_Read(false);
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task MembershipTable_Consul_ReadAll_Insert_ReadAll()
        {
            await MembershipTable_ReadAll_Insert_ReadAll(false);
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task MembershipTable_Consul_UpdateRow()
        {
            await MembershipTable_UpdateRow(false);
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task MembershipTable_Consul_UpdateRowInParallel()
        {
            await MembershipTable_UpdateRowInParallel(false);
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task MembershipTable_Consul_UpdateIAmAlive()
        {
            await MembershipTable_UpdateIAmAlive(false);
        }
    }
}
