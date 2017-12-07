using System.Runtime.CompilerServices;
using Orleans.CodeGeneration;
using Orleans.Providers.Streams.Common;
using Orleans.Providers.Streams.Generator;

[assembly: InternalsVisibleTo("NonSilo.Tests")]
[assembly: InternalsVisibleTo("Tester.AzureUtils")]
[assembly: InternalsVisibleTo("Tester.SQLUtils")]
[assembly: InternalsVisibleTo("TesterInternal")]
[assembly: InternalsVisibleTo("TestExtensions")]
[assembly: InternalsVisibleTo("UnitTestGrains")]
[assembly: InternalsVisibleTo("UnitTests")]

[assembly: KnownAssembly(typeof(EventSequenceTokenV2))]

[assembly: GenerateSerializer(typeof(EventSequenceTokenV2))]
[assembly: GenerateSerializer(typeof(GeneratedBatchContainer))]
