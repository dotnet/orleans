using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Orleans.TelemetryConsumers.Counters")]
[assembly: InternalsVisibleTo("Orleans.TestingHost")]
[assembly: InternalsVisibleTo("Orleans.TestingHost.AppDomain")]

[assembly: InternalsVisibleTo("AWSUtils.Tests")]
[assembly: InternalsVisibleTo("LoadTestGrains")]
[assembly: InternalsVisibleTo("NonSilo.Tests")]
[assembly: InternalsVisibleTo("Tester.AzureUtils")]
[assembly: InternalsVisibleTo("Tester.AdoNet")]
[assembly: InternalsVisibleTo("Tester.Reminders.Cosmos.Migration")]
[assembly: InternalsVisibleTo("TesterInternal")]
[assembly: InternalsVisibleTo("TestInternalGrains")]

[assembly: InternalsVisibleTo("Orleans.Persistence.Migration")]

// Mocking libraries
[assembly: InternalsVisibleTo("DynamicProxyGenAssembly2")]