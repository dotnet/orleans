using System.Runtime.CompilerServices;
using Orleans.CodeGeneration;
using Orleans.Providers.Streams.AzureQueue;

[assembly: InternalsVisibleTo("Tester")]
[assembly: InternalsVisibleTo("Orleans.Streaming.AzureStorage.Migration")]

[assembly: GenerateSerializer(typeof(AzureQueueBatchContainerV2))]
