using System.Runtime.CompilerServices;
using Microsoft.FSharp.Core;
using Orleans.CodeGeneration;
using UnitTests.FSharpTypes;

[assembly: InternalsVisibleTo("TestInternalGrains")]
[assembly: InternalsVisibleTo("TesterInternal")]
[assembly: InternalsVisibleTo("Tester")]
[assembly: InternalsVisibleTo("DefaultCluster.Tests")]

// generate Orleans serializers for types in FSharp.core.dll
[assembly: KnownAssembly(typeof(FSharpOption<>))]

[assembly: KnownAssembly(typeof(SingleCaseDU))]
