using System.Runtime.CompilerServices;
using Microsoft.FSharp.Core;
using Orleans.CodeGeneration;

[assembly: InternalsVisibleTo("TestInternalGrains")]
[assembly: InternalsVisibleTo("Tester")]
[assembly: InternalsVisibleTo("TesterInternal")]
[assembly: InternalsVisibleTo("DefaultCLuster.Tests")]

// generate Orleans serializers for types in FSharp.core.dll
[assembly: KnownAssembly(typeof(FSharpOption<>))]