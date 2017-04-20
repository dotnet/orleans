using System.Runtime.CompilerServices;
using Orleans.CodeGeneration;

[assembly: InternalsVisibleTo("UnitTests")]
[assembly: InternalsVisibleTo("UnitTestGrains")]
[assembly: InternalsVisibleTo("Tester.AzureUtils")]
[assembly: InternalsVisibleTo("Tester.SQLUtils")]
[assembly: InternalsVisibleTo("AWSUtils.Tests")]

[assembly: SkipCodeGeneration]
