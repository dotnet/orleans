using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Orleans.Concurrency;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.TestingHost;
using TestExtensions;
using UnitTests.GrainInterfaces;
using UnitTests.Grains;
using Xunit;

namespace UnitTests.ActivationsLifeCycleTests;

// Keep lifecycle probes out of parallel test collections.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class StatelessWorkerActivationTimerCollection
{
    public const string Name = "Stateless worker activation timers";
}

[TestSuite("BVT"), TestProvider("None"), TestCategory("BVT")]
[Collection(StatelessWorkerActivationTimerCollection.Name)]
public sealed class StatelessWorkerActivationTimerTests
{
    private const string TimerInventoryChild = "ORLEANS_STATELESS_WORKER_TIMER_INVENTORY_CHILD";

    [Fact]
    public async Task CompletedActivationsReleaseTheirTimeoutTimers()
    {
        // Nonparallel collections still share timers left over by earlier collections.
        // A fresh process makes the exact inventory independent of those timers expiring.
        if (Environment.GetEnvironmentVariable(TimerInventoryChild) != "1")
        {
            await RunTimerInventoryInIsolatedProcess();
            return;
        }

        await using var fixture = new Fixture();
        await fixture.InitializeAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        var services = fixture.HostedCluster.Silos[0].ServiceProvider;
        var grains = services.GetRequiredService<IGrainFactory>();
        var tracker = services.GetRequiredService<StatelessWorkerSingleActivationTracker>();
        var stable = grains.GetGrain<IStatelessWorkerSingleActivationGrain>(0);
        await stable.DoWork().WaitAsync(token);
        var timers = Timer.ActiveCount;

        for (var call = 0; call < 100; ++call)
        {
            await stable.DoWork().WaitAsync(token);
        }
        Assert.Equal(1, tracker.Current);
        Assert.Equal(timers, Timer.ActiveCount);

        for (var key = 1; key <= 100; ++key)
        {
            await grains.GetGrain<IStatelessWorkerSingleActivationGrain>(key).DoWork().WaitAsync(token);
        }
        Assert.Equal(101, tracker.Current);
        Assert.Equal(timers, Timer.ActiveCount);
    }

    private static async Task RunTimerInventoryInIsolatedProcess()
    {
        var assemblyPath = typeof(StatelessWorkerActivationTimerTests).Assembly.Location;
        var startInfo = new ProcessStartInfo(Environment.ProcessPath!)
        {
            ArgumentList =
            {
                "--filter-method", $"{typeof(StatelessWorkerActivationTimerTests).FullName}.{nameof(CompletedActivationsReleaseTheirTimeoutTimers)}",
                "--minimum-expected-tests", "1",
                "--progress", "off", "--ansi", "off", "--output", "Minimal",
            },
            WorkingDirectory = Path.GetDirectoryName(assemblyPath)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        // Match the existing isolated hot-reload test's host selection on both apphost and dotnet launches.
        if (string.Equals(Path.GetFileNameWithoutExtension(startInfo.FileName), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            startInfo.ArgumentList.Insert(0, assemblyPath);
        }

        startInfo.Environment[TimerInventoryChild] = "1";
        using var process = Process.Start(startInfo)!;
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken);
            var output = await outputTask;
            var error = await errorTask;
            Assert.True(process.ExitCode == 0,
                $"Isolated timer inventory failed with exit code {process.ExitCode}.{Environment.NewLine}"
                + output[^Math.Min(output.Length, 4000)..] + Environment.NewLine
                + error[^Math.Min(error.Length, 4000)..]);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }

            await Task.WhenAll(outputTask, errorTask);
        }
    }

    [Fact]
    public async Task ActivationTimeoutCancelsTheCallbackAndAwaitsItsCompletion()
    {
        await using var isolated = new Fixture { ActivationTimeout = TimeSpan.FromSeconds(1) };
        await isolated.InitializeAsync();
        var token = TestContext.Current.CancellationToken;
        var registry = isolated.HostedCluster.Silos[0].ServiceProvider.GetRequiredService<LifecycleCallbackRegistry>();
        var grain = isolated.GrainFactory.GetGrain<IStatelessActivationTimeoutGrain>(
            $"{LifecycleCallback.OnActivateAsync}/{Guid.NewGuid():N}");
        var id = grain.GetGrainId();
        registry.Track(id);
        var call = grain.Ping();
        var probe = await registry.Get(id).Probes.Reader.ReadAsync(token).AsTask().WaitAsync(TimeSpan.FromSeconds(30), token);
        try
        {
            await probe.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30), token);
            await probe.Canceled.Task.WaitAsync(TimeSpan.FromSeconds(30), token);
            Assert.False(call.IsCompleted);
            Assert.False(probe.ScopeDisposed);
            Assert.False(probe.Context.Deactivated.IsCompleted);
            Assert.Equal(1, registry.Get(id).ActivationCount);
        }
        finally
        {
            probe.Release.TrySetResult();
        }
        await probe.Context.Deactivated.WaitAsync(TimeSpan.FromSeconds(30), token);
        Assert.True(probe.ScopeDisposed);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call.WaitAsync(TimeSpan.FromSeconds(30), token));
        await grain.Ping().WaitAsync(TimeSpan.FromSeconds(30), token);
        Assert.Equal(2, registry.Get(id).ActivationCount);
    }

    public sealed class Fixture : BaseInProcessTestClusterFixture
    {
        public TimeSpan ActivationTimeout { get; init; } = TimeSpan.FromMinutes(5);

        protected override void ConfigureTestCluster(InProcessTestClusterBuilder builder)
        {
            builder.Options.InitialSilosCount = 1;
            // Freeze background clocks so periodic maintenance cannot change the native timer inventory.
            var clock = new FakeTimeProvider();
            builder.ConfigureSilo((_, silo) =>
            {
                silo.Services.UseTimeProviderForBackgroundAreas(clock);
                silo.Services.AddSingleton<StatelessWorkerSingleActivationTracker>();
                silo.Services.AddSingleton<LifecycleCallbackRegistry>();
                silo.Services.AddScoped<LifecycleCallbackProbe>();
                silo.Services.Configure<StatelessWorkerOptions>(options => options.RemoveIdleWorkers = false);
                silo.Services.Configure<GrainCollectionOptions>(options => options.ActivationTimeout = ActivationTimeout);
            });
            builder.ConfigureClient(client => client.Services.UseTimeProviderForBackgroundAreas(clock));
        }

        public override async ValueTask DisposeAsync()
        {
            HostedCluster.Silos[0].ServiceProvider.GetRequiredService<LifecycleCallbackRegistry>().ReleaseAll();
            await base.DisposeAsync();
        }
    }
}

public interface IStatelessActivationTimeoutGrain : IGrainWithStringKey
{
    Task Ping();
}

[StatelessWorker(1)]
public sealed class StatelessActivationTimeoutGrain(LifecycleCallbackProbe probe) : Grain, IStatelessActivationTimeoutGrain
{
    public Task Ping() => Task.CompletedTask;

    public override Task OnActivateAsync(CancellationToken cancellationToken) =>
        probe.Run(LifecycleCallback.OnActivateAsync, cancellationToken);
}
