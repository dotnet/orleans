#nullable enable
using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Runtime.GrainDirectory;
using Orleans.TestingHost;
using Xunit;

namespace UnitTests.GrainDirectory;

[TestSuite("BVT")]
[TestProvider("None")]
[TestCategory("BVT"), TestCategory("Directory")]
public sealed class GrainDirectoryOptionsTests
{
    [Fact]
    public async Task PartitionsPerSilo_IsConfigurable()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var builder = new InProcessTestClusterBuilder(1);
        builder.ConfigureSilo((_, siloBuilder) =>
        {
#pragma warning disable ORLEANSEXP003
            siloBuilder.Configure<GrainDirectoryOptions>(options => options.PartitionsPerSilo = 3);
            siloBuilder.AddDistributedGrainDirectory();
#pragma warning restore ORLEANSEXP003
        });

        var cluster = builder.Build();
        try
        {
            await cluster.DeployAsync(cancellationToken);
            var membershipService = cluster.Silos[0].ServiceProvider.GetRequiredService<DirectoryMembershipService>();

            Assert.Equal(3, membershipService.PartitionsPerSilo);
        }
        finally
        {
            await cluster.DisposeAsync();
        }
    }
}
