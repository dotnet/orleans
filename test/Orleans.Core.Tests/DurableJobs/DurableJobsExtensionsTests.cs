using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Orleans.DurableJobs;
using Orleans.Hosting;
using Orleans.Runtime;
using Xunit;

namespace NonSilo.Tests.ScheduledJobs;

[TestCategory("DurableJobs")]
public class DurableJobsExtensionsTests
{
    [Fact]
    public void AddDurableJobs_RegistersHandlerRegistryAndReceiverExtensionFactory_ResolveWithoutError()
    {
        var grainContext = Substitute.For<IGrainContext>();
        grainContext.GrainId.Returns(GrainId.Create("test", "grain-1"));

        var grainContextAccessor = Substitute.For<IGrainContextAccessor>();
        grainContextAccessor.GrainContext.Returns(grainContext);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions();
        services.AddMetrics();
        services.AddSingleton<OrleansInstruments>();
        services.AddSingleton(grainContextAccessor);

        services.AddDurableJobs();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        // The 5-line diff under test: DurableJobHandlerRegistry is scoped, forwarded as
        // IDurableJobHandlerRegistry, and passed as the 3rd constructor argument to the
        // DurableJobReceiverExtension factory (previously a 2-arg constructor).
        var registryAsInterface = scope.ServiceProvider.GetRequiredService<IDurableJobHandlerRegistry>();
        var registryConcrete = scope.ServiceProvider.GetRequiredService<DurableJobHandlerRegistry>();
        Assert.Same(registryConcrete, registryAsInterface);

        var extension = scope.ServiceProvider.GetRequiredKeyedService<IGrainExtension>(typeof(IDurableJobReceiverExtension));
        Assert.IsType<DurableJobReceiverExtension>(extension);

        // Resolving twice in the same scope must not construct a second registry instance
        // (the receiver extension and any other scoped consumer must observe the same registry).
        var registryAsInterfaceAgain = scope.ServiceProvider.GetRequiredService<IDurableJobHandlerRegistry>();
        Assert.Same(registryAsInterface, registryAsInterfaceAgain);
    }

    [Fact]
    public void AddDurableJobs_ProducesDistinctHandlerRegistryInstances_AcrossScopes()
    {
        var grainContext = Substitute.For<IGrainContext>();
        grainContext.GrainId.Returns(GrainId.Create("test", "grain-1"));

        var grainContextAccessor = Substitute.For<IGrainContextAccessor>();
        grainContextAccessor.GrainContext.Returns(grainContext);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions();
        services.AddMetrics();
        services.AddSingleton<OrleansInstruments>();
        services.AddSingleton(grainContextAccessor);

        services.AddDurableJobs();

        using var provider = services.BuildServiceProvider();

        using var scopeA = provider.CreateScope();
        using var scopeB = provider.CreateScope();

        var registryA = scopeA.ServiceProvider.GetRequiredService<IDurableJobHandlerRegistry>();
        var registryB = scopeB.ServiceProvider.GetRequiredService<IDurableJobHandlerRegistry>();

        Assert.NotSame(registryA, registryB);
    }

    [Fact]
    public void AddDurableJobs_PassesScopedHandlerRegistryIntoReceiverExtension()
    {
        var grainContext = Substitute.For<IGrainContext>();
        grainContext.GrainId.Returns(GrainId.Create("test", "grain-1"));

        var grainContextAccessor = Substitute.For<IGrainContextAccessor>();
        grainContextAccessor.GrainContext.Returns(grainContext);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions();
        services.AddMetrics();
        services.AddSingleton<OrleansInstruments>();
        services.AddSingleton(grainContextAccessor);

        services.AddDurableJobs();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var expectedRegistry = scope.ServiceProvider.GetRequiredService<DurableJobHandlerRegistry>();
        var extension = Assert.IsType<DurableJobReceiverExtension>(
            scope.ServiceProvider.GetRequiredKeyedService<IGrainExtension>(typeof(IDurableJobReceiverExtension)));

        var field = typeof(DurableJobReceiverExtension).GetField("_featureHandlers", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);

        var actualRegistry = Assert.IsType<DurableJobHandlerRegistry>(field!.GetValue(extension));
        Assert.Same(expectedRegistry, actualRegistry);
    }
}
