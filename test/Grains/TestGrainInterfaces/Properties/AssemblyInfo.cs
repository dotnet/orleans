using System.Runtime.CompilerServices;
using Orleans.CodeGeneration;
using UnitTests.GrainInterfaces;

[assembly: InternalsVisibleTo("TesterInternal")]
[assembly: InternalsVisibleTo("DefaultCluster.Tests")]
[assembly: InternalsVisibleTo("NonSilo.Tests")]
[assembly: GenerateSerializer(typeof(SomeTypeDerivedFromTypeUsedInGrainInterface))]
