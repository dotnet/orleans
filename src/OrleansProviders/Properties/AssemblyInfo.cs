using System.Runtime.CompilerServices;
using Orleans.CodeGeneration;
using Orleans.Providers.Streams.Common;
using Orleans.Providers.Streams.Generator;

[assembly: InternalsVisibleTo("UnitTests")]
[assembly: InternalsVisibleTo("UnitTestGrains")]
[assembly: InternalsVisibleTo("TesterInternal")]
[assembly: InternalsVisibleTo("NonSilo.Tests")]
[assembly: InternalsVisibleTo("TestExtensions")]
[assembly: InternalsVisibleTo("Tester.AzureUtils")]
[assembly: InternalsVisibleTo("Tester.SQLUtils")]

[assembly: KnownAssembly(typeof(EventSequenceTokenV2), TreatTypesAsSerializable = true)]

[assembly: GenerateSerializer(typeof(EventSequenceTokenV2))]
[assembly: GenerateSerializer(typeof(GeneratedBatchContainer))]
