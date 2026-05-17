using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.EventSourcing.CustomStorage;
using Orleans.Journaling;
using Orleans.Journaling.Json;
using Orleans.Storage;
using Orleans.TestingHost;
using TestGrains;
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
        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            builder.AddSiloBuilderConfigurator<TestSiloConfigurator>();
        }

        private class TestSiloConfigurator : ISiloConfigurator
        {
            private static readonly VolatileJournalStorageProvider JournalStorageProvider = new();

            public void Configure(ISiloBuilder hostBuilder)
            {
                // we use a slowed-down memory storage provider
                hostBuilder
                    .AddLogStorageBasedLogConsistencyProvider("LogStorage")
                    .AddStateStorageBasedLogConsistencyProvider("StateStorage")
                    .AddJournaledStateBasedLogConsistencyProvider("JournaledState")
                    .UseJsonJournalFormat(options =>
                    {
                        options.SerializerOptions.IncludeFields = true;
                        options.AddTypeInfoResolver(EventSourcingTestsJsonContext.Default);
                        options.ConfigurePolymorphicType<object>()
                            .AddDerivedType<UpdateA>()
                            .AddDerivedType<UpdateB>()
                            .AddDerivedType<IncrementA>()
                            .AddDerivedType<AddReservation>()
                            .AddDerivedType<RemoveReservation>();
                    })
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

    [JsonSourceGenerationOptions(IncludeFields = true)]
    [JsonSerializable(typeof(DateTime))]
    [JsonSerializable(typeof(int))]
    [JsonSerializable(typeof(object))]
    [JsonSerializable(typeof(string))]
    [JsonSerializable(typeof(uint))]
    [JsonSerializable(typeof(UpdateA))]
    [JsonSerializable(typeof(UpdateB))]
    [JsonSerializable(typeof(IncrementA))]
    [JsonSerializable(typeof(AddReservation))]
    [JsonSerializable(typeof(RemoveReservation))]
    internal sealed partial class EventSourcingTestsJsonContext : JsonSerializerContext;
}
#pragma warning restore ORLEANSEXP005
