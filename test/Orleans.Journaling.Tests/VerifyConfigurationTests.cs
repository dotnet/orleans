using DiffEngine;
using Xunit;

namespace Orleans.Journaling.Tests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestCategory("BVT")]
public sealed class VerifyConfigurationTests
{
    [Fact]
    public void SnapshotDiffToolsAreDisabled() => Assert.True(DiffRunner.Disabled);
}
