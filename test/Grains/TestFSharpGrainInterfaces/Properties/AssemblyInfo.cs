using System.Runtime.CompilerServices;
using Microsoft.FSharp.Core;
using Orleans.CodeGeneration;
using UnitTests.FSharpTypes;

[assembly: InternalsVisibleTo("TesterInternal")]
[assembly: InternalsVisibleTo("DefaultCluster.Tests")]
[assembly: InternalsVisibleTo("NonSilo.Tests")]

// generate Orleans serializers for types in FSharp.core.dll
[assembly: KnownAssembly(typeof(FSharpOption<>))]

[assembly: KnownAssembly(typeof(SingleCaseDU))]
