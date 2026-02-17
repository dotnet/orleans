using System.Runtime.CompilerServices;
using Xunit;

// Disable XUnit concurrency limit
[assembly: CollectionBehavior(MaxParallelThreads = -1)]

[assembly: InternalsVisibleTo("Orleans.Runtime.Internal.Tests")]
