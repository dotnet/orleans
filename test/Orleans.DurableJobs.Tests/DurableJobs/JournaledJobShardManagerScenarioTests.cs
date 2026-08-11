using TestExtensions;
using Xunit;

namespace Orleans.DurableJobs.Tests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableJobs")]
[TestCategory("DurableJobs")]
public sealed class JournaledJobShardManagerScenarioTests(VolatileJobShardManagerTestFixture fixture)
    : JobShardManagerTestsRunner(fixture), IClassFixture<VolatileJobShardManagerTestFixture>;
