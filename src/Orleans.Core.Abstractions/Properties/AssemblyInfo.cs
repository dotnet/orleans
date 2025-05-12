using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Orleans.Core")]
[assembly: InternalsVisibleTo("Orleans.Runtime")]
[assembly: InternalsVisibleTo("Orleans.TestingHost")]
[assembly: InternalsVisibleTo("Orleans.TestingHost.AppDomain")]

[assembly: InternalsVisibleTo("DefaultCluster.Tests")]
[assembly: InternalsVisibleTo("NonSilo.Tests")]
[assembly: InternalsVisibleTo("Tester.AzureUtils")]
[assembly: InternalsVisibleTo("Tester.Reminders.Cosmos")]
[assembly: InternalsVisibleTo("AWSUtils.Tests")]
[assembly: InternalsVisibleTo("TesterInternal")]
[assembly: InternalsVisibleTo("TestInternalGrainInterfaces")]
[assembly: InternalsVisibleTo("TestInternalGrains")]

[assembly: InternalsVisibleTo("Orleans.Persistence.Migration")]
[assembly: InternalsVisibleTo("Orleans.Reminders.Cosmos")]
[assembly: InternalsVisibleTo("Tester.AzureUtils.Migration")]

// Mocking libraries
[assembly: InternalsVisibleTo("DynamicProxyGenAssembly2")]