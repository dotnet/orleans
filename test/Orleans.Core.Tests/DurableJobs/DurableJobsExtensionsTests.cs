using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Orleans.DurableJobs;
using Orleans.Hosting;
using Orleans.Metadata;
using Orleans.Runtime;
using Xunit;

namespace NonSilo.Tests.ScheduledJobs;

[TestCategory("BVT"), TestCategory("DurableJobs")]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableJobs")]
public class DurableJobsExtensionsTests
{
    [Fact]
    public void AddDurableJobs_RegistersEagerGrainContextConfiguration()
    {
        var services = new ServiceCollection();

        services.AddDurableJobs();

        var descriptor = Assert.Single(
            services,
            service => service.ServiceType == typeof(IConfigureGrainContextProvider)
                && service.ImplementationType == typeof(DurableJobGrainContextConfigurator));
        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);

        var configurator = new DurableJobGrainContextConfigurator();
        Assert.True(configurator.TryGetConfigurator(
            GrainType.Create("test"),
            new GrainProperties(ImmutableDictionary.Create<string, string>(StringComparer.Ordinal)),
            out var resolved));
        Assert.Same(configurator, resolved);

        var context = Substitute.For<IGrainContext>();
        context.ObservableLifecycle.Returns(Substitute.For<IGrainLifecycle>());
        configurator.Configure(context);
        var componentTypes = context.ReceivedCalls()
            .Where(static call => call.GetMethodInfo().Name == nameof(IGrainContext.SetComponent))
            .Select(static call => call.GetMethodInfo().GetGenericArguments()[0])
            .ToList();
        Assert.Equal(
            [
                typeof(DurableJobExecutionLifetime),
                typeof(ActivationDeactivationCoordinator),
                typeof(IActivationDeactivationParticipant),
            ],
            componentTypes);
    }

    [Fact]
    public async Task AddDurableJobs_RegistryAndReceiverShareScope_WhileSeparateScopesAreIsolated()
    {
        var grainContext = Substitute.For<IGrainContext>();
        grainContext.GrainId.Returns(GrainId.Create("test", "grain-1"));
        grainContext.GrainInstance.Returns(new object());
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
        var handlerA = Substitute.For<IDurableJobFeatureHandler>();
        var handlerB = Substitute.For<IDurableJobFeatureHandler>();
        handlerA.ExecuteJobAsync(Arg.Any<IJobRunContext>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult(DurableJobRunResult.Completed));
        handlerB.ExecuteJobAsync(Arg.Any<IJobRunContext>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult(DurableJobRunResult.Completed));
        handlerA.CanHandle("feature").Returns(true);
        handlerB.CanHandle("feature").Returns(true);
        registryA.Register(handlerA);
        registryB.Register(handlerB);

        var extensionA = scopeA.ServiceProvider.GetRequiredKeyedService<IGrainExtension>(typeof(IDurableJobReceiverExtension));
        var extensionB = scopeB.ServiceProvider.GetRequiredKeyedService<IGrainExtension>(typeof(IDurableJobReceiverExtension));
        var contextA = CreateContext("run-a");
        var contextB = CreateContext("run-b");

        Assert.NotSame(registryA, registryB);
        Assert.Equal(
            DurableJobRunStatus.Completed,
            (await ((IDurableJobReceiverExtension)extensionA).HandleDurableJobAsync(contextA, CancellationToken.None)).Status);
        Assert.Equal(
            DurableJobRunStatus.Completed,
            (await ((IDurableJobReceiverExtension)extensionB).HandleDurableJobAsync(contextB, CancellationToken.None)).Status);
        await handlerA.Received(1).ExecuteJobAsync(contextA, Arg.Any<CancellationToken>());
        await handlerA.DidNotReceive().ExecuteJobAsync(contextB, Arg.Any<CancellationToken>());
        await handlerB.Received(1).ExecuteJobAsync(contextB, Arg.Any<CancellationToken>());
        await handlerB.DidNotReceive().ExecuteJobAsync(contextA, Arg.Any<CancellationToken>());
    }

    [Fact]
    public void AddDurableJobs_WhenRegistryIsPreRegistered_RejectsReplacement()
    {
        var services = new ServiceCollection();
        services.AddScoped<IDurableJobHandlerRegistry, ReplacementRegistry>();

        var exception = Assert.Throws<InvalidOperationException>(services.AddDurableJobs);

        Assert.Contains("cannot be replaced or decorated", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReceiverResolution_WhenRegistryIsDecoratedAfterConfiguration_FailsExplicitly()
    {
        var grainContextAccessor = Substitute.For<IGrainContextAccessor>();
        grainContextAccessor.GrainContext.Returns(Substitute.For<IGrainContext>());
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions();
        services.AddMetrics();
        services.AddSingleton<OrleansInstruments>();
        services.AddSingleton(grainContextAccessor);
        services.AddDurableJobs();
        services.AddScoped<IDurableJobHandlerRegistry, ReplacementRegistry>();
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var exception = Assert.Throws<InvalidOperationException>(
            () => scope.ServiceProvider.GetRequiredKeyedService<IGrainExtension>(typeof(IDurableJobReceiverExtension)));

        Assert.Contains("cannot be replaced or decorated", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReceiverResolution_WhenRegistryReplacementImplementsLookup_FailsExplicitly()
    {
        var grainContextAccessor = Substitute.For<IGrainContextAccessor>();
        grainContextAccessor.GrainContext.Returns(Substitute.For<IGrainContext>());
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions();
        services.AddMetrics();
        services.AddSingleton<OrleansInstruments>();
        services.AddSingleton(grainContextAccessor);
        services.AddDurableJobs();
        services.AddScoped<IDurableJobHandlerRegistry, LookupReplacementRegistry>();
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var exception = Assert.Throws<InvalidOperationException>(
            () => scope.ServiceProvider.GetRequiredKeyedService<IGrainExtension>(typeof(IDurableJobReceiverExtension)));

        Assert.Contains("cannot be replaced or decorated", exception.Message, StringComparison.Ordinal);
    }

    private static IJobRunContext CreateContext(string runId)
    {
        var context = Substitute.For<IJobRunContext>();
        context.RunId.Returns(runId);
        context.DequeueCount.Returns(1);
        context.Job.Returns(new DurableJob
        {
            Id = "job-1",
            Name = "feature",
            DueTime = DateTimeOffset.UtcNow,
            TargetGrainId = GrainId.Create("test", "grain-1"),
            ShardId = "shard-1"
        });
        return context;
    }

    private sealed class ReplacementRegistry : IDurableJobHandlerRegistry
    {
        public void Register(IDurableJobFeatureHandler handler)
        {
        }

        public void Register(IDurableJobFeatureHandler handler, bool requiresTurnIsolation)
        {
        }
    }

    private sealed class LookupReplacementRegistry : IDurableJobHandlerRegistry, IDurableJobHandlerLookup
    {
        public CancellationToken ExecutionToken => CancellationToken.None;

        public void Register(IDurableJobFeatureHandler handler)
        {
        }

        public void Register(IDurableJobFeatureHandler handler, bool requiresTurnIsolation)
        {
        }

        public Task<TResult> StartExecution<TResult>(
            Func<CancellationToken, Task<TResult>> factory,
            bool holdTurnIsolation) =>
            factory(CancellationToken.None);

        public bool TryGetHandler(string jobName, [NotNullWhen(true)] out IDurableJobFeatureHandler? handler)
        {
            handler = null;
            return false;
        }

        public bool TryGetIsolatedHandler(string jobName, [NotNullWhen(true)] out IDurableJobFeatureHandler? handler)
        {
            handler = null;
            return false;
        }
    }
}
