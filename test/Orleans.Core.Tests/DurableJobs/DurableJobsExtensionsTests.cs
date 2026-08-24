using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Orleans.DurableJobs;
using Orleans.Hosting;
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
        handlerA.CanHandle(Arg.Is<DurableJob>(static job => job.Name == "feature")).Returns(true);
        handlerB.CanHandle(Arg.Is<DurableJob>(static job => job.Name == "feature")).Returns(true);
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
    }

    private sealed class LookupReplacementRegistry : IDurableJobHandlerRegistry, IDurableJobHandlerLookup
    {
        public void Register(IDurableJobFeatureHandler handler)
        {
        }

        public bool TryGetHandler(DurableJob job, [NotNullWhen(true)] out IDurableJobFeatureHandler? handler)
        {
            handler = null;
            return false;
        }
    }
}
