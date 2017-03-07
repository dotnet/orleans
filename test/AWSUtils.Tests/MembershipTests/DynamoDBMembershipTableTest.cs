﻿using System.Threading.Tasks;
using AWSUtils.Tests.StorageTests;
using Orleans;
using Orleans.Messaging;
using Orleans.Runtime;
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
    public class DynamoDBMembershipTableTest : MembershipTableTestsBase, IClassFixture<DynamoDBStorageTestsFixture>
    {
        public DynamoDBMembershipTableTest(ConnectionStringFixture fixture, TestEnvironmentFixture environment) : base(fixture, environment)
        {
            LogManager.AddTraceLevelOverride("DynamoDBDataManager", Severity.Verbose3);
            LogManager.AddTraceLevelOverride("OrleansSiloInstanceManager", Severity.Verbose3);
            LogManager.AddTraceLevelOverride("Storage", Severity.Verbose3);
        }

        protected override IMembershipTable CreateMembershipTable(Logger logger)
        {
            if (!AWSTestConstants.IsDynamoDbAvailable)
                throw new SkipException("Unable to connect to AWS DynamoDB simulator");

            return new DynamoDBMembershipTable();
        }

        protected override IGatewayListProvider CreateGatewayListProvider(Logger logger)
        {
            return new DynamoDBGatewayListProvider();
        }

        protected override Task<string> GetConnectionString()
        {
            return Task.FromResult(AWSTestConstants.IsDynamoDbAvailable ? "Service=http://localhost:8000;" : null);
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task MembershipTable_DynamoDB_GetGateways()
        {
            await MembershipTable_GetGateways();
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task MembershipTable_DynamoDB_ReadAll_EmptyTable()
        {
            await MembershipTable_ReadAll_EmptyTable();
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task MembershipTable_DynamoDB_InsertRow()
        {
            await MembershipTable_InsertRow(false);
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task MembershipTable_DynamoDB_ReadRow_Insert_Read()
        {
            await MembershipTable_ReadRow_Insert_Read(false);
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task MembershipTable_DynamoDB_ReadAll_Insert_ReadAll()
        {
            await MembershipTable_ReadAll_Insert_ReadAll(false);
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task MembershipTable_DynamoDB_UpdateRow()
        {
            await MembershipTable_UpdateRow(false);
        }

        [SkippableFact]
        public async Task MembershipTable_DynamoDB_UpdateRowInParallel()
        {
            await MembershipTable_UpdateRowInParallel(false);
        }

        [SkippableFact]
        public async Task MembershipTable_DynamoDB_UpdateIAmAlive()
        {
            await MembershipTable_UpdateIAmAlive(false);
        }
    }
}
