using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.EventSourcing.CustomStorage;
using Orleans.Journaling;
using Orleans.Storage;
using Orleans.TestingHost;
using TestExtensions;

#pragma warning disable ORLEANSEXP005
namespace Tester.EventSourcingTests
{
    /// <summary>
    /// We use a special fixture for event sourcing tests 
    /// so we can add the required log consistency providers, and 
    /// do more tracing
    /// </summary>
    public class EventSourcingClusterFixture : BaseTestClusterFixture
    {
        private const string OrleansBinaryJournalFormatKey = "orleans-binary";

        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            builder.AddSiloBuilderConfigurator<TestSiloConfigurator>();
        }

        private class TestSiloConfigurator : ISiloConfigurator
        {
            private static readonly VolatileJournalStorageProvider JournalStorageProvider = new(Options.Create(new JournaledStateManagerOptions
            {
                JournalFormatKey = OrleansBinaryJournalFormatKey
            }));

            public void Configure(ISiloBuilder hostBuilder)
            {
                // we use a slowed-down memory storage provider
                hostBuilder
                    .AddLogStorageBasedLogConsistencyProvider("LogStorage")
                    .AddStateStorageBasedLogConsistencyProvider("StateStorage")
                    .AddJournaledStateBasedLogConsistencyProvider("JournaledState")
                    .AddCustomStorageBasedLogConsistencyProvider("CustomStoragePrimaryCluster")
                    .ConfigureLogging(builder =>
                    {
                        builder.AddFilter(typeof(MemoryGrainStorage).FullName, LogLevel.Debug);
                        builder.AddFilter(typeof(LogConsistencyProvider).Namespace, LogLevel.Debug);
                    })
                    .AddMemoryGrainStorageAsDefault()
                    .AddMemoryGrainStorage("AzureStore")
                    .AddMemoryGrainStorage("MemoryStore")
                    .AddFaultInjectionMemoryStorage("SlowMemoryStore", options=>options.NumStorageGrains = 10, faultyOptions => faultyOptions.Latency = TimeSpan.FromMilliseconds(15));

                hostBuilder.Services.AddSingleton<IJournalStorageProvider>(JournalStorageProvider);
            }
        }
    }
}
#pragma warning restore ORLEANSEXP005
