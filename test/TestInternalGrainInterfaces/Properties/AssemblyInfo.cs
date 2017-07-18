using System.Runtime.CompilerServices;
#if !EXCLUDEFSHARP
using Microsoft.FSharp.Core;
#endif
using Orleans.CodeGeneration;

[assembly: InternalsVisibleTo("TestInternalGrains")]
[assembly: InternalsVisibleTo("TesterInternal")]
[assembly: InternalsVisibleTo("Tester")]
[assembly: InternalsVisibleTo("DefaultCluster.Tests")]
[assembly: InternalsVisibleTo("UnitTests")]
[assembly: InternalsVisibleTo("UnitTestGrainInterfaces")]
[assembly: InternalsVisibleTo("UnitTestGrains")]

// generate Orleans serializers for types in FSharp.core.dll
#if !EXCLUDEFSHARP
[assembly: KnownAssembly(typeof(FSharpOption<>))]
#endif
