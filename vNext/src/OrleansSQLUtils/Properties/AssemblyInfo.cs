using System.Runtime.CompilerServices;
using Orleans.CodeGeneration;

[assembly: InternalsVisibleTo("UnitTests")]
[assembly: InternalsVisibleTo("TesterInternal")]
[assembly: InternalsVisibleTo("UnitTestGrains")]
[assembly: InternalsVisibleTo("Tester.SQLUtils")]
[assembly: SkipCodeGeneration]
