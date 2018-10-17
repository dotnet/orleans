using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Orleans.Runtime;
using Orleans.Transactions.Abstractions;
using Orleans.Transactions.AzureStorage;
using Orleans.Transactions.AzureStorage.Tests;
using Orleans.Transactions.Testkit.Xunit;
using Xunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.WindowsAzure.Storage.Table;
using TestExtensions;

namespace Orleans.Transactions.Azure.Tests
{
    public class TestState
    {
        public int state { get; set; } = 1;
    }

    public class AzureTransactionalStateStorageTests : TransactionalStateStorageTestRunnerXunit<TestState>, IClassFixture<TestFixture>
    {
        public AzureTransactionalStateStorageTests(TestFixture fixture)
            :base(()=>StateStorageFactory(fixture), ()=>new TestState(), fixture.GrainFactory)
        {
        }

        private static async Task<ITransactionalStateStorage<TestState>> StateStorageFactory(TestFixture fixture)
        {
            var tableManager = new AzureTableDataManager<TableEntity>($"StateStorageTests", TestDefaultConfiguration.DataConnectionString, 
               NullLoggerFactory.Instance);
            await tableManager.InitTableAsync().ConfigureAwait(false);
            var table = tableManager.Table;
            var jsonSettings = TransactionalStateFactory.GetJsonSerializerSettings(
                fixture.HostedCluster.ServiceProvider.GetRequiredService<ITypeResolver>(),
                fixture.GrainFactory);
            var stateStorage = new AzureTableTransactionalStateStorage<TestState>(table, "testpartition", jsonSettings, 
                NullLoggerFactory.Instance.CreateLogger<AzureTableTransactionalStateStorage<TestState>>());
            return stateStorage;
        }
    }
}
