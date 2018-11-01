﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Orleans.Runtime;
using Orleans.Transactions.Abstractions;
using Orleans.Transactions.AzureStorage;
using Orleans.Transactions.AzureStorage.Tests;
using Orleans.Transactions.TestKit.xUnit;
using Xunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Table;
using TestExtensions;
using Xunit.Abstractions;

namespace Orleans.Transactions.Azure.Tests
{
    public class TestState : TestKit.Base.ITestState
    {
        public int state { get; set; } = 1;
    }

    public class AzureTransactionalStateStorageTests : TransactionalStateStorageTestRunnerxUnit<TestState>, IClassFixture<TestFixture>
    {
        private const string tableName = "StateStorageTests";
        private const string partition = "testpartition";
        public AzureTransactionalStateStorageTests(TestFixture fixture, ITestOutputHelper testOutput)
            :base(()=>StateStorageFactory(fixture), ()=>new TestState(), fixture.GrainFactory, testOutput)
        {
        }

        private static async Task<ITransactionalStateStorage<TestState>> StateStorageFactory(TestFixture fixture)
        {
            var table = await InitTableAsync(NullLogger.Instance);
            var jsonSettings = TransactionalStateFactory.GetJsonSerializerSettings(
                fixture.HostedCluster.ServiceProvider.GetRequiredService<ITypeResolver>(),
                fixture.GrainFactory);
            var stateStorage = new AzureTableTransactionalStateStorage<TestState>(table, $"{partition}{DateTime.UtcNow.Ticks}", jsonSettings, 
                NullLoggerFactory.Instance.CreateLogger<AzureTableTransactionalStateStorage<TestState>>());
            return stateStorage;
        }

        private static async Task<CloudTable> InitTableAsync(ILogger logger)
        {
            try
            {
                CloudTableClient tableCreationClient = GetCloudTableCreationClient(logger);
                CloudTable tableRef = tableCreationClient.GetTableReference(tableName);
                bool didCreate = await tableRef.CreateIfNotExistsAsync();


                logger.Info($"{(didCreate ? "Created" : "Attached to")} Azure storage table {tableName}", (didCreate ? "Created" : "Attached to"));
                return tableRef;
            }
            catch (Exception exc)
            {
                logger.LogError($"Could not initialize connection to storage table {tableName}", exc);
                throw;
            }
        }

        private static CloudTableClient GetCloudTableCreationClient(ILogger logger)
        {
            try
            {
                CloudStorageAccount storageAccount = CloudStorageAccount.Parse(TestDefaultConfiguration.DataConnectionString);
                CloudTableClient creationClient = storageAccount.CreateCloudTableClient();
                // Values supported can be AtomPub, Json, JsonFullMetadata or JsonNoMetadata with Json being the default value
                creationClient.DefaultRequestOptions.PayloadFormat = TablePayloadFormat.JsonNoMetadata;
                return creationClient;
            }
            catch (Exception exc)
            {
                logger.LogError("Error creating CloudTableCreationClient.", exc);
                throw;
            }
        }
    }
}
