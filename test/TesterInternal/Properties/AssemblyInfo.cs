using System.Runtime.CompilerServices;
using Orleans.CodeGeneration;

[assembly: InternalsVisibleTo("UnitTests")]
[assembly: InternalsVisibleTo("Tester.AzureUtils")]
[assembly: InternalsVisibleTo("Tester.SQLUtils")]
[assembly: InternalsVisibleTo("AWSUtils.Tests")]
[assembly: InternalsVisibleTo("GoogleUtils.Tests")]
[assembly: InternalsVisibleTo("UnitTestGrains")]
[assembly: SkipCodeGeneration]
