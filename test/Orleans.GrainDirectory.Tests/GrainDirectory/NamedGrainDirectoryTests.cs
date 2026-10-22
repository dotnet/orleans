using Microsoft.Extensions.DependencyInjection;
using Orleans.GrainDirectory;
using Orleans.Hosting;
using Orleans.Placement;
using Orleans.Runtime;
using Orleans.Runtime.GrainDirectory;
using Orleans.TestingHost;
using Xunit;

namespace UnitTests.GrainDirectory;

[TestSuite("BVT")]
[TestProvider("None")]
[TestCategory("BVT"), TestCategory("Directory")]
public sealed class NamedGrainDirectoryTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("named")]
    public async Task DistributedDirectoryRegistration_PreservesSingleActivation(string? directoryName)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var builder = new InProcessTestClusterBuilder(2);
        builder.Options.UseTestClusterGrainDirectory = false;
        builder.ConfigureSilo((_, siloBuilder) =>
        {
            if (directoryName is not null)
            {
                siloBuilder.UseLegacyGrainDirectory();
            }

            siloBuilder.AddDistributedGrainDirectory(directoryName);
        });

        await using var cluster = builder.Build();
        await cluster.DeployAsync(cancellationToken);
        await cluster.WaitForLivenessToStabilizeAsync().WaitAsync(cancellationToken);
        await cluster.WaitForClusterManifestToStabilizeAsync().WaitAsync(cancellationToken);

        foreach (var silo in cluster.Silos)
        {
            var resolver = silo.ServiceProvider.GetRequiredService<GrainDirectoryResolver>();
            var directory = silo.ServiceProvider.GetRequiredKeyedService<IGrainDirectory>(
                directoryName ?? GrainDirectoryAttribute.DEFAULT_GRAIN_DIRECTORY);
            Assert.IsType<DistributedGrainDirectory>(directory);
            if (directoryName is null)
            {
                Assert.Same(directory, resolver.DefaultGrainDirectory);
            }
            else
            {
                Assert.Null(resolver.DefaultGrainDirectory);
            }
        }

        for (var key = 0L; key < 10; key++)
        {
            var firstSilo = cluster.Silos[0];
            var secondSilo = cluster.Silos[1];
            if (directoryName is not null)
            {
                var grainId = firstSilo.ServiceProvider.GetRequiredService<IGrainFactory>()
                    .GetGrain<IDefaultDirectoryIsolationGrain>(key).GetGrainId();
                var directory = firstSilo.ServiceProvider.GetRequiredService<LocalGrainDirectory>();
                var owner = directory.CalculateGrainDirectoryPartition(grainId);

                // Register remotely first, then query from the owner to exercise both directory paths.
                firstSilo = cluster.Silos.Single(silo => !silo.SiloAddress.Equals(owner));
                secondSilo = cluster.Silos.Single(silo => silo.SiloAddress.Equals(owner));
            }

            var firstFactory = firstSilo.ServiceProvider.GetRequiredService<IGrainFactory>();
            var secondFactory = secondSilo.ServiceProvider.GetRequiredService<IGrainFactory>();
            var first = firstFactory.GetGrain<IDefaultDirectoryIsolationGrain>(key);
            var second = secondFactory.GetGrain<IDefaultDirectoryIsolationGrain>(key);
            var address = await first.GetAddress().AsTask().WaitAsync(cancellationToken);
            Assert.Equal(firstSilo.SiloAddress, address.SiloAddress);
            Assert.Equal(address, await second.GetAddress().AsTask().WaitAsync(cancellationToken));

            if (directoryName is not null)
            {
                var namedFirst = firstFactory.GetGrain<INamedDirectoryIsolationGrain>(key);
                var namedSecond = secondFactory.GetGrain<INamedDirectoryIsolationGrain>(key);
                var namedAddress = await namedFirst.GetAddress().AsTask().WaitAsync(cancellationToken);
                Assert.Equal(firstSilo.SiloAddress, namedAddress.SiloAddress);
                Assert.Equal(namedAddress, await namedSecond.GetAddress().AsTask().WaitAsync(cancellationToken));
            }
        }
    }
}

public interface IDefaultDirectoryIsolationGrain : IGrainWithIntegerKey
{
    ValueTask<GrainAddress> GetAddress();
}

public interface INamedDirectoryIsolationGrain : IGrainWithIntegerKey
{
    ValueTask<GrainAddress> GetAddress();
}

[PreferLocalPlacement]
public sealed class DefaultDirectoryIsolationGrain : Grain, IDefaultDirectoryIsolationGrain
{
    public ValueTask<GrainAddress> GetAddress() => new(GrainContext.Address);
}

[PreferLocalPlacement]
[GrainDirectory(GrainDirectoryName = "named")]
public sealed class NamedDirectoryIsolationGrain : Grain, INamedDirectoryIsolationGrain
{
    public ValueTask<GrainAddress> GetAddress() => new(GrainContext.Address);
}
