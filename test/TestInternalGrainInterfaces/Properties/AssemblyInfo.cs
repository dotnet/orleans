using System.Runtime.CompilerServices;
using Microsoft.FSharp.Core;
using Orleans.CodeGeneration;

[assembly: InternalsVisibleTo("TestInternalGrains")]
[assembly: InternalsVisibleTo("TesterInternal")]
[assembly: InternalsVisibleTo("Tester")]
[assembly: InternalsVisibleTo("DefaultCluster.Tests")]
[assembly: InternalsVisibleTo("UnitTests")]
[assembly: InternalsVisibleTo("UnitTestGrainInterfaces")]
[assembly: InternalsVisibleTo("UnitTestGrains")]

// generate Orleans serializers for types in FSharp.core.dll
[assembly: KnownAssembly(typeof(FSharpOption<>))]
