using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Orleans.Streaming")]
[assembly: InternalsVisibleTo("Orleans.Reminders")]
[assembly: InternalsVisibleTo("Orleans.DurableJobs")]
[assembly: InternalsVisibleTo("Orleans.Journaling")]
[assembly: InternalsVisibleTo("Orleans.TestingHost")]

[assembly: InternalsVisibleTo("Orleans.AWS.Tests")]
[assembly: InternalsVisibleTo("LoadTestGrains")]
[assembly: InternalsVisibleTo("Orleans.Core.Tests")]
[assembly: InternalsVisibleTo("Orleans.Azure.Tests")]
[assembly: InternalsVisibleTo("Orleans.AdoNet.Tests")]
[assembly: InternalsVisibleTo("Orleans.Runtime.Internal.Tests")]
[assembly: InternalsVisibleTo("Orleans.Placement.Tests")]
[assembly: InternalsVisibleTo("Orleans.GrainDirectory.Tests")]
[assembly: InternalsVisibleTo("Orleans.Streaming.Tests")]
[assembly: InternalsVisibleTo("TestInternalGrains")]
[assembly: InternalsVisibleTo("Benchmarks")]

// Mocking libraries
[assembly: InternalsVisibleTo("DynamicProxyGenAssembly2")]
