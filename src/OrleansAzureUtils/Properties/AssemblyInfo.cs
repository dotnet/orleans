using System.Runtime.CompilerServices;
using Orleans.CodeGeneration;
using Orleans.Providers.Streams.AzureQueue;

[assembly: InternalsVisibleTo("NonSilo.Tests")]
[assembly: InternalsVisibleTo("Tester.AzureUtils")]
[assembly: InternalsVisibleTo("TesterInternal")]
[assembly: InternalsVisibleTo("UnitTestGrains")]
[assembly: InternalsVisibleTo("UnitTests")]

[assembly: GenerateSerializer(typeof(AzureQueueBatchContainerV2))]
