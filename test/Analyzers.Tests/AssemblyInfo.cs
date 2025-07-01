using Xunit;

/// <summary>
/// Assembly-level configuration for Orleans analyzer tests.
/// Disables XUnit's concurrency limit to allow maximum parallel test execution,
/// which is safe for analyzer tests as they don't share state or external resources.
/// </summary>
[assembly: CollectionBehavior(MaxParallelThreads = -1)]
