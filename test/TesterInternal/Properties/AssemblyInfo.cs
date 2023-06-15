using System.Runtime.CompilerServices;

// Disable XUnit concurrency limit
[assembly: Xunit.CollectionBehavior(MaxParallelThreads = -1)]

[assembly: InternalsVisibleTo("Tester.AzureUtils")]
[assembly: InternalsVisibleTo("Tester.Cosmos")]
[assembly: InternalsVisibleTo("Tester.AdoNet")]
[assembly: InternalsVisibleTo("Tester.Redis")]
[assembly: InternalsVisibleTo("AWSUtils.Tests")]
[assembly: InternalsVisibleTo("GoogleUtils.Tests")]