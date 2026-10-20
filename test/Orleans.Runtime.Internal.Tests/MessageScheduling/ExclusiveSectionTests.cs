using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Concurrency;
using Orleans.Runtime;
using Orleans.TestingHost;
using TestExtensions;
using Xunit;

namespace UnitTests.MessageScheduling;

/// <summary>
/// Verifies the exclusive sections which <see cref="IGrainRequestAdmission.EnterExclusiveSectionAsync"/> opens: while a section
/// is open, the activation starts only requests which always interleave and requests on the holder's call chain, and sections
/// open one at a time, in the order in which they're requested.
/// </summary>
[TestSuite("BVT"), TestProvider("None"), TestArea("Runtime"), TestCategory("BVT")]
public class ExclusiveSectionTests(ExclusiveSectionTests.Fixture fixture) : IClassFixture<ExclusiveSectionTests.Fixture>
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Section_HoldsBackOrdinaryRequest_UntilDisposed()
    {
        var (grain, log) = fixture.CreateGrain();
        await Run(grain.OpenSection("a", allowCallChainReentrancy: false));

        var ping = grain.Ping("p");
        await fixture.AssertHeldAsync(grain, log, "p");

        await Run(grain.CloseSection("a"));
        await Run(ping);
        Assert.Equal(["a:open", "barrier:p", "a:closing", "p"], log.Snapshot());
    }

    [Fact]
    public async Task Section_StartsAlwaysInterleaveRequest()
    {
        var (grain, log) = fixture.CreateGrain();
        await Run(grain.OpenSection("a", allowCallChainReentrancy: false));

        await Run(grain.InterleavingPing("i"));

        await Run(grain.CloseSection("a"));
        Assert.Equal(["a:open", "i", "a:closing"], log.Snapshot());
    }

    [Fact]
    public async Task Sections_OpenOneAtATime_InRequestOrder()
    {
        var (grain, log) = fixture.CreateGrain();
        await Run(grain.OpenSection("a", allowCallChainReentrancy: false));
        await Run(grain.RequestSectionFromTimer("b"));
        await Run(grain.RequestSectionFromScheduler("c"));

        await Run(grain.CloseSection("a"));
        await Run(log.WaitAsync("b:open"));
        await Run(grain.CloseSection("b"));
        await Run(log.WaitAsync("c:open"));
        await Run(grain.CloseSection("c"));

        Assert.Equal(
            ["a:open", "b:requested", "c:requested", "a:closing", "b:open", "b:closing", "c:open", "c:closing"],
            log.Snapshot());
    }

    [Fact]
    public async Task CanceledWait_Throws_AndExitSkipsIt()
    {
        var (grain, log) = fixture.CreateGrain();
        await Run(grain.OpenSection("a", allowCallChainReentrancy: false));
        await Run(grain.RequestCanceledSection("x"));
        await Run(grain.RequestSectionFromScheduler("c"));
        var ping = grain.Ping("p");
        await fixture.AssertHeldAsync(grain, log, "p");

        await Run(grain.CloseSection("a"));
        await Run(log.WaitAsync("c:open"));
        await fixture.AssertHeldAsync(grain, log, "p", barrier: "barrier:p2");
        await Run(grain.CloseSection("c"));
        await Run(ping);

        Assert.Equal(
            ["a:open", "x:requested", "x:canceled", "c:requested", "barrier:p", "a:closing", "c:open", "barrier:p2", "c:closing", "p"],
            log.Snapshot());
        Assert.IsAssignableFrom<OperationCanceledException>(log.Faults["x"]);
    }

    [Fact]
    public async Task CanceledWait_LeavesSectionToClose()
    {
        var (grain, log) = fixture.CreateGrain();
        await Run(grain.OpenSection("a", allowCallChainReentrancy: false));
        await Run(grain.RequestCanceledSection("x"));
        var ping = grain.Ping("p");
        await fixture.AssertHeldAsync(grain, log, "p");

        await Run(grain.CloseSection("a"));
        await Run(ping);

        Assert.Equal(["a:open", "x:requested", "x:canceled", "barrier:p", "a:closing", "p"], log.Snapshot());
    }

    [Fact]
    public async Task Deactivation_FaultsWaitingSections()
    {
        var (grain, log) = fixture.CreateGrain();
        await Run(grain.OpenSection("a", allowCallChainReentrancy: false));
        await Run(grain.RequestSectionFromScheduler("b"));
        var activation = fixture.GetActivation(grain);

        await Run(grain.Deactivate());
        await Run(log.WaitAsync("b:faulted"));
        await Run(activation.Deactivated);

        Assert.Equal(["a:open", "b:requested", "b:faulted"], log.Snapshot());
        Assert.IsType<InvalidOperationException>(log.Faults["b"]);
    }

    [Fact]
    public async Task Section_StartsCallbackOnHolderCallChain()
    {
        var (grain, log) = fixture.CreateGrain();
        var caller = fixture.GrainFactory.GetGrain<IExclusiveSectionCallerGrain>(Guid.NewGuid());

        await Run(grain.OpenSectionAndCallBack(caller, "cb"));

        Assert.Equal(["cb:open", "cb", "cb:closing"], log.Snapshot());
    }

    [Fact]
    public async Task Section_HoldsBackRequestOnOtherCallChain()
    {
        var (grain, log) = fixture.CreateGrain();
        var caller = fixture.GrainFactory.GetGrain<IExclusiveSectionCallerGrain>(Guid.NewGuid());
        await Run(grain.OpenSection("a", allowCallChainReentrancy: true));

        // The callback carries the call chain of a reentrant section which another request on this activation opened.
        var releaseCallback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        log.CallbackRelease = releaseCallback;
        var call = grain.CallBackInReentrantSection(caller, "b");
        try
        {
            // The outer interleaving request can queue before the callback exists. Wait until the caller receives it.
            await Run(log.WaitAsync("b:received"));
            await Run(grain.InterleavingPing("before-callback"));
            Assert.Equal(0, fixture.GetActivation(grain).WaitingCount);
            Assert.DoesNotContain("b", log.Snapshot());

            releaseCallback.TrySetResult();
            await fixture.AssertHeldAsync(grain, log, "b");
        }
        finally
        {
            releaseCallback.TrySetResult();
            await Run(grain.CloseSection("a"));
        }

        await Run(call);
        Assert.Equal(["a:open", "b:received", "before-callback", "barrier:b", "a:closing", "b"], log.Snapshot());
    }

    [Fact]
    public async Task OpenSection_KeepsActivationFromIdleCollection()
    {
        var (grain, _) = fixture.CreateGrain();
        await Run(grain.OpenSection("a", allowCallChainReentrancy: false));
        var activation = fixture.GetActivation(grain);

        await fixture.WaitForNoRequestsAsync(activation);
        Assert.False(activation.IsInactive);

        await Run(grain.CloseSection("a"));
        await fixture.WaitForNoRequestsAsync(activation);
        Assert.True(activation.IsInactive);
    }

    private static Task Run(Task task) => task.WaitAsync(Timeout, Cancellation);

    public sealed class Fixture : BaseInProcessTestClusterFixture
    {
        private ExclusiveSectionProbe Probe => HostedCluster.Silos[0].ServiceProvider.GetRequiredService<ExclusiveSectionProbe>();

        public (IExclusiveSectionGrain Grain, ExclusiveSectionLog Log) CreateGrain()
        {
            var key = Guid.NewGuid();
            return (GrainFactory.GetGrain<IExclusiveSectionGrain>(key), Probe.GetLog(key));
        }

        internal ActivationData GetActivation(IExclusiveSectionGrain grain)
        {
            Assert.True(HostedCluster.TryGetGrainContext(grain.GetGrainId(), out var context));
            return Assert.IsType<ActivationData>(context);
        }

        /// <summary>
        /// Asserts that the activation holds back the request which records <paramref name="name"/>, once it waits.
        /// </summary>
        /// <remarks>
        /// The activation evaluates waiting requests in arrival order, so by the time it starts the barrier, which always
        /// interleaves and is sent after the held request waits, it has evaluated the held request.
        /// </remarks>
        public async Task AssertHeldAsync(IExclusiveSectionGrain grain, ExclusiveSectionLog log, string name, string? barrier = null)
        {
            var activation = GetActivation(grain);
            await WaitUntilAsync(() => activation.WaitingCount > 0, $"request '{name}' to wait on {grain.GetGrainId()}");

            await Run(grain.InterleavingPing(barrier ?? $"barrier:{name}"));
            Assert.DoesNotContain(name, log.Snapshot());
            Assert.Equal(1, activation.WaitingCount);
        }

        internal Task WaitForNoRequestsAsync(ActivationData activation) =>
            WaitUntilAsync(() => !activation.IsCurrentlyExecuting && activation.WaitingCount == 0, $"requests to complete on {activation.GrainId}");

        protected override void ConfigureTestCluster(InProcessTestClusterBuilder builder)
        {
            builder.Options.InitialSilosCount = 1;
            builder.ConfigureSilo((_, silo) => silo.Services.AddSingleton<ExclusiveSectionProbe>());
        }

        private static async Task WaitUntilAsync(Func<bool> condition, string phase)
        {
            var stopwatch = Stopwatch.StartNew();
            while (!condition())
            {
                if (stopwatch.Elapsed > Timeout)
                {
                    throw new TimeoutException($"Timed out after {Timeout} waiting for {phase}.");
                }

                await Task.Delay(10, Cancellation);
            }
        }
    }
}

public sealed class ExclusiveSectionProbe
{
    private readonly ConcurrentDictionary<Guid, ExclusiveSectionLog> _logs = new();

    public ExclusiveSectionLog GetLog(Guid grainKey) => _logs.GetOrAdd(grainKey, _ => new());
}

/// <summary>
/// The events which one test grain records, in order, and the exceptions with which its section waits ended.
/// </summary>
public sealed class ExclusiveSectionLog
{
    public TaskCompletionSource? CallbackRelease { get; set; }
    private readonly List<string> _events = [];
    private readonly ConcurrentDictionary<string, TaskCompletionSource> _recorded = new();

    public ConcurrentDictionary<string, Exception> Faults { get; } = new();

    public void Record(string name)
    {
        lock (_events)
        {
            _events.Add(name);
        }

        Recorded(name).TrySetResult();
    }

    public Task WaitAsync(string name) => Recorded(name).Task;

    public string[] Snapshot()
    {
        lock (_events)
        {
            return [.. _events];
        }
    }

    private TaskCompletionSource Recorded(string name) => _recorded.GetOrAdd(name, _ => new(TaskCreationOptions.RunContinuationsAsynchronously));
}

public interface IExclusiveSectionGrain : IGrainWithGuidKey
{
    Task OpenSection(string name, bool allowCallChainReentrancy);

    [AlwaysInterleave]
    Task CloseSection(string name);

    Task Ping(string name);

    [AlwaysInterleave]
    Task InterleavingPing(string name);

    [AlwaysInterleave]
    Task RequestSectionFromTimer(string name);

    [AlwaysInterleave]
    Task RequestSectionFromScheduler(string name);

    [AlwaysInterleave]
    Task RequestCanceledSection(string name);

    Task OpenSectionAndCallBack(IExclusiveSectionCallerGrain caller, string name);

    [AlwaysInterleave]
    Task CallBackInReentrantSection(IExclusiveSectionCallerGrain caller, string name);

    [AlwaysInterleave]
    Task Deactivate();
}

public interface IExclusiveSectionCallerGrain : IGrainWithGuidKey
{
    Task CallBack(IExclusiveSectionGrain target, string name);
}

public sealed class ExclusiveSectionCallerGrain(ExclusiveSectionProbe probe) : Grain, IExclusiveSectionCallerGrain
{
    public async Task CallBack(IExclusiveSectionGrain target, string name)
    {
        var log = probe.GetLog(target.GetPrimaryKey());
        if (log.CallbackRelease is { } release)
        {
            log.Record($"{name}:received");
            await release.Task;
        }

        await target.Ping(name);
    }
}

public sealed class ExclusiveSectionGrain(ExclusiveSectionProbe probe) : Grain, IExclusiveSectionGrain
{
    // Sections are stored from continuations which run off the activation's scheduler.
    private readonly ConcurrentDictionary<string, IDisposable> _sections = new();

    private ExclusiveSectionLog Log => probe.GetLog(this.GetPrimaryKey());

    private IGrainRequestAdmission Admission => GrainContext.GetComponent<IGrainRequestAdmission>()!;

    public async Task OpenSection(string name, bool allowCallChainReentrancy)
    {
        using var reentrancy = allowCallChainReentrancy ? RequestContext.AllowCallChainReentrancy() : default;
        _sections[name] = await Admission.EnterExclusiveSectionAsync(CancellationToken.None);
        Log.Record($"{name}:open");
    }

    public Task CloseSection(string name)
    {
        Log.Record($"{name}:closing");
        Assert.True(_sections.TryRemove(name, out var section));
        section.Dispose();
        return Task.CompletedTask;
    }

    public Task Ping(string name)
    {
        Log.Record(name);
        return Task.CompletedTask;
    }

    public Task InterleavingPing(string name) => Ping(name);

    public Task RequestSectionFromTimer(string name)
    {
        this.RegisterGrainTimer(
            () => RunSectionAsync(name),
            new GrainTimerCreationOptions { DueTime = TimeSpan.Zero, Period = Timeout.InfiniteTimeSpan, Interleave = true });
        return Log.WaitAsync($"{name}:requested");
    }

    public Task RequestSectionFromScheduler(string name)
    {
        _ = Task.Factory.StartNew(() => RunSectionAsync(name), CancellationToken.None, TaskCreationOptions.None, TaskScheduler.Current).Unwrap();
        return Log.WaitAsync($"{name}:requested");
    }

    public async Task RequestCanceledSection(string name)
    {
        using var cancellation = new CancellationTokenSource();
        var entering = Admission.EnterExclusiveSectionAsync(cancellation.Token);
        Log.Record($"{name}:requested");
        cancellation.Cancel();
        try
        {
            _sections[name] = await entering;
            Log.Record($"{name}:open");
        }
        catch (Exception exception)
        {
            Log.Faults[name] = exception;
            Log.Record($"{name}:canceled");
        }
    }

    public async Task OpenSectionAndCallBack(IExclusiveSectionCallerGrain caller, string name)
    {
        using var reentrancy = RequestContext.AllowCallChainReentrancy();
        using (await Admission.EnterExclusiveSectionAsync(CancellationToken.None))
        {
            Log.Record($"{name}:open");
            await caller.CallBack(this.AsReference<IExclusiveSectionGrain>(), name);
            Log.Record($"{name}:closing");
        }
    }

    public async Task CallBackInReentrantSection(IExclusiveSectionCallerGrain caller, string name)
    {
        using var reentrancy = RequestContext.AllowCallChainReentrancy();
        await caller.CallBack(this.AsReference<IExclusiveSectionGrain>(), name);
    }

    public Task Deactivate()
    {
        DeactivateOnIdle();
        return Task.CompletedTask;
    }

    // Requests a section from code which isn't a grain method, and records it once it opens or its wait faults.
    private Task RunSectionAsync(string name)
    {
        var log = Log;
        var entering = Admission.EnterExclusiveSectionAsync(CancellationToken.None).AsTask();
        log.Record($"{name}:requested");

        // The continuation runs on the thread pool, so it runs even once the activation's scheduler stops running its work.
        return entering.ContinueWith(
            task =>
            {
                if (task.Exception is { } exception)
                {
                    log.Faults[name] = exception.InnerException!;
                    log.Record($"{name}:faulted");
                    return;
                }

                _sections[name] = task.Result;
                log.Record($"{name}:open");
            },
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default);
    }
}
