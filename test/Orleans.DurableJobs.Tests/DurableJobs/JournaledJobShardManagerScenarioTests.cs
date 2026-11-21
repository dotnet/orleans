using TestExtensions;
using Xunit;

namespace Orleans.Durable.Jobs.Tests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableJobs")]
[TestCategory("DurableJobs")]
public sealed class JournaledJobShardManagerScenarioTests(VolatileJobShardManagerTestFixture fixture)
    : JobShardManagerTestsRunner(fixture), IClassFixture<VolatileJobShardManagerTestFixture>;
