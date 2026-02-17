using System.Runtime.CompilerServices;

// Disable XUnit concurrency limit
[assembly: Xunit.CollectionBehavior(MaxParallelThreads = -1)]

[assembly: InternalsVisibleTo("Orleans.Azure.Tests")]
[assembly: InternalsVisibleTo("Orleans.Cosmos.Tests")]
[assembly: InternalsVisibleTo("Orleans.AdoNet.Tests")]
[assembly: InternalsVisibleTo("Orleans.Redis.Tests")]
[assembly: InternalsVisibleTo("Orleans.AWS.Tests")]
[assembly: InternalsVisibleTo("GoogleUtils.Tests")]
[assembly: InternalsVisibleTo("Orleans.Streaming.NATS.Tests")]