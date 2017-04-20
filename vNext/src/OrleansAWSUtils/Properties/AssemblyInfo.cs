using System.Reflection;
using System.Runtime.CompilerServices;
using Orleans.CodeGeneration;

[assembly: InternalsVisibleTo("UnitTests")]
[assembly: InternalsVisibleTo("AWSUtils.Tests")]
[assembly: InternalsVisibleTo("UnitTestGrains")]
[assembly: SkipCodeGeneration]
