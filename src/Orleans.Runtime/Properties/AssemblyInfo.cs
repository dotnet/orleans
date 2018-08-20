using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Orleans.TelemetryConsumers.Counters")]
[assembly: InternalsVisibleTo("Orleans.TestingHost")]
[assembly: InternalsVisibleTo("Orleans.TestingHost.AppDomain")]
[assembly: InternalsVisibleTo("OrleansCounterControl")]

// For IActivationSelector and related types.
[assembly: InternalsVisibleTo("Orleans.Clustering.ServiceFabric")]

[assembly: InternalsVisibleTo("AWSUtils.Tests")]
[assembly: InternalsVisibleTo("LoadTestGrains")]
[assembly: InternalsVisibleTo("NonSilo.Tests")]
[assembly: InternalsVisibleTo("Tester.AzureUtils")]
[assembly: InternalsVisibleTo("Tester.AdoNet")]
[assembly: InternalsVisibleTo("TesterInternal")]
[assembly: InternalsVisibleTo("TestInternalGrains")]

// Legacy provider support
[assembly: InternalsVisibleTo("Orleans.Runtime.Legacy")]
