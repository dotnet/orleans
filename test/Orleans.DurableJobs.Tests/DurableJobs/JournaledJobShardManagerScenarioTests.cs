using TestExtensions;
using Xunit;

namespace Orleans.DurableJobs.Tests;

[TestCategory("DurableJobs")]
public sealed class JournaledJobShardManagerScenarioTests(VolatileJobShardManagerTestFixture fixture)
    : JobShardManagerTestsRunner(fixture), IClassFixture<VolatileJobShardManagerTestFixture>;
