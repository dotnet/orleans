using System.Runtime.CompilerServices;
using Orleans.CodeGeneration;
using Orleans.Providers.Streams.Common;
using Orleans.Providers.Streams.Generator;

[assembly: InternalsVisibleTo("NonSilo.Tests")]
[assembly: InternalsVisibleTo("Tester.AzureUtils")]
[assembly: InternalsVisibleTo("Tester.AdoNet")]
[assembly: InternalsVisibleTo("TesterInternal")]
[assembly: InternalsVisibleTo("TestExtensions")]
[assembly: InternalsVisibleTo("DefaultCluster.Tests")]

[assembly: KnownAssembly(typeof(EventSequenceTokenV2), TreatTypesAsSerializable = true)]

[assembly: GenerateSerializer(typeof(EventSequenceTokenV2))]
[assembly: GenerateSerializer(typeof(GeneratedBatchContainer))]
