using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Orleans.Core")]
[assembly: InternalsVisibleTo("Orleans.Runtime")]
[assembly: InternalsVisibleTo("Orleans.TestingHost")]
[assembly: InternalsVisibleTo("Orleans.Streaming")]
[assembly: InternalsVisibleTo("Orleans.Streaming.Abstractions")]
[assembly: InternalsVisibleTo("Orleans.Reminders")]

[assembly: InternalsVisibleTo("Orleans.DefaultCluster.Tests")]
[assembly: InternalsVisibleTo("Orleans.Core.Tests")]
[assembly: InternalsVisibleTo("Orleans.Streaming.EventHubs.Tests")]
[assembly: InternalsVisibleTo("Orleans.Azure.Tests")]
[assembly: InternalsVisibleTo("Orleans.AWS.Tests")]
[assembly: InternalsVisibleTo("Orleans.Runtime.Tests")]
[assembly: InternalsVisibleTo("Orleans.Runtime.Internal.Tests")]
[assembly: InternalsVisibleTo("Orleans.Placement.Tests")]
[assembly: InternalsVisibleTo("Orleans.GrainDirectory.Tests")]
[assembly: InternalsVisibleTo("TestInternalGrainInterfaces")]
[assembly: InternalsVisibleTo("TestInternalGrains")]

// Mocking libraries
[assembly: InternalsVisibleTo("DynamicProxyGenAssembly2")]
