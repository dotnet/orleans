using System.Reflection;
using System.Runtime.CompilerServices;
using Orleans.CodeGeneration;

[assembly: InternalsVisibleTo("UnitTests")]
[assembly: InternalsVisibleTo("TesterInternal")]
[assembly: InternalsVisibleTo("UnitTestGrains")]
[assembly: InternalsVisibleTo("Tester.AzureUtils")]
[assembly: SkipCodeGeneration]
