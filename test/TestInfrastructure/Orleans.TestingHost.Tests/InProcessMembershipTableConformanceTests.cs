using Orleans.Clustering.TestKit;
using Orleans.TestingHost.InProcess;
using TestExtensions;
using UnitTests.MembershipTests;

namespace Orleans.TestingHost.Tests;

[TestCategory("BVT"), TestCategory("Membership")]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("TestingHost")]
public sealed class InProcessMembershipTableConformanceTests : MembershipTableConformanceTestsBase
{
    protected override MembershipTableTestFixture CreateConformanceFixture()
    {
        var owners = new Dictionary<string, InProcessMembershipTable>(StringComparer.Ordinal);
        return new MembershipTableTestFixture(
            nameof(InProcessMembershipTable),
            clusterId =>
            {
                if (!owners.TryGetValue(clusterId, out var owner))
                {
                    owner = new InProcessMembershipTable(clusterId);
                    owners.Add(clusterId, owner);
                }

                return owner.CreateClient();
            },
            async (clusterId, cancellationToken) =>
                (await owners[clusterId].ReadAllAsync(cancellationToken)).Members.Count == 0);
    }
}
