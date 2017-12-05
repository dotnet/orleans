using System.Runtime.CompilerServices;
using Orleans.CodeGeneration;
using Orleans.Providers.Streams.AzureQueue;

[assembly: InternalsVisibleTo("Tester")]

[assembly: GenerateSerializer(typeof(AzureQueueBatchContainerV2))]
