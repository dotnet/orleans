using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Orleans.Streaming")]
[assembly: InternalsVisibleTo("Orleans.Reminders")]
[assembly: InternalsVisibleTo("Orleans.TestingHost")]

[assembly: InternalsVisibleTo("AWSUtils.Tests")]
[assembly: InternalsVisibleTo("LoadTestGrains")]
[assembly: InternalsVisibleTo("NonSilo.Tests")]
[assembly: InternalsVisibleTo("Tester.AzureUtils")]
[assembly: InternalsVisibleTo("Tester.AdoNet")]
[assembly: InternalsVisibleTo("TesterInternal")]
[assembly: InternalsVisibleTo("TestInternalGrains")]

// Mocking libraries
[assembly: InternalsVisibleTo("DynamicProxyGenAssembly2")]