using Orleans;
using Orleans.Runtime;
using UnitTests.GrainInterfaces;
using UnitTests.Grains;
using Xunit;
using Azure.Data.Tables;
using TestExtensions;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Reminders.AzureStorage.Storage.Reminders;
using Orleans.Runtime.ReminderService;
using Orleans.Persistence.Migration;
using Orleans.Persistence.AzureStorage.Migration.Reminders.Storage;
using Orleans.Streams;
using Orleans.Providers.Streams.AzureQueue;

namespace Tester.AzureUtils.Migration.Abstractions
{
    public abstract class MigrationStreamingAzureQueueTests : MigrationBaseTests
    {
        const int baseId = 800;

        protected MigrationStreamingAzureQueueTests(BaseAzureTestClusterFixture fixture)
            : base(fixture)
        {
        }

        [SkippableFact]
        public async Task Streaming_PushesDataIntoPredeterminedAzureQueue()
        {
            var streamId = Guid.NewGuid();
            var streamNamespace = $"test-{baseId}-123";

            var streamProvider = ServiceProvider.GetRequiredServiceByName<IStreamProvider>("AzureQueueProvider");
            var stream = streamProvider.GetStream<StreamDataType>(streamId, streamNamespace);
            var data = GenerateStreamData();

            await stream.OnNextAsync(data);
        }

        private static StreamDataType GenerateStreamData()
        {
            return new StreamDataType
            {
                Id = Guid.NewGuid().ToString(),
                Name = "TestStreamData",
                Version = 1
            };
        }
    }

    public class StreamDataType
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public int Version { get; set; }
    }
}
