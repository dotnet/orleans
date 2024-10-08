#nullable enable
using Microsoft.Extensions.DependencyInjection;
using Orleans.GrainDirectory;
using Orleans.Runtime.GrainDirectory;
using Orleans.TestingHost;
using Tester.Directories;
using TestExtensions;
using Xunit;
using Xunit.Abstractions;

namespace UnitTests.GrainDirectory;

[TestCategory("BVT"), TestCategory("Directory")]
public sealed class DefaultGrainDirectoryTests(DefaultClusterFixture fixture, ITestOutputHelper output)
    : GrainDirectoryTests<IGrainDirectory>(output), IClassFixture<DefaultClusterFixture>
{
    private readonly TestCluster _testCluster = fixture.HostedCluster;
    private InProcessSiloHandle Primary => (InProcessSiloHandle)_testCluster.Primary;

    protected override IGrainDirectory CreateGrainDirectory() =>
        Primary.SiloHost.Services.GetRequiredService<GrainDirectoryResolver>().DefaultGrainDirectory;
}
