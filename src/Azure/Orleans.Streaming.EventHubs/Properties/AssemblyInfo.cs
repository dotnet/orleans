using System.Runtime.CompilerServices;
using Orleans.CodeGeneration;
using Orleans.ServiceBus.Providers;

[assembly: InternalsVisibleTo("ServiceBus.Tests")]

// Fail to build if a serializer is not generated for EventHubSequenceTokenV2
[assembly: GenerateSerializer(typeof(EventHubSequenceToken))]