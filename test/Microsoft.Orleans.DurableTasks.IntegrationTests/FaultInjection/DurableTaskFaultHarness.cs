using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Orleans.DurableTasks;
using Orleans.DurableTasks.Protocol;
using Xunit;

namespace Microsoft.Orleans.DurableTasks.IntegrationTests.FaultInjection;

/// <summary>
/// Hosts an in-process cluster whose journal storage, outbox deliveries, inbox participants, and messaging wake-ups run
/// through a <see cref="DurableTaskFaultInjector"/>, and drives workflows to completion through injected faults.
/// </summary>
/// <remarks>
/// The harness owns the Durable Jobs clock: while it waits for a task, it advances the clock in steps, so delays, scheduled
/// resumes, and outbox retries fall due without real-time waits. Each step is far shorter than Messaging's
/// <c>OutboxRetryTimeout</c>, so retries of an injected delivery failure never expire while a run makes progress.
/// </remarks>
internal sealed class DurableTaskFaultHarness : IAsyncLifetime
{
    public static readonly TimeSpan ClockStep = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan RunTimeout = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan QuietPeriod = TimeSpan.FromMilliseconds(250);
    private static readonly PollingOptions Poll = new() { PollTimeout = TimeSpan.FromMilliseconds(50) };

    public DurableTaskFaultHarness(string journalFormatKey, Action<ISiloBuilder>? configureSilo = null)
    {
        Injector = new();
        Gates = new();
        Fixture = new(journalFormatKey, silo =>
        {
            Injector.Configure(silo);
            silo.Services.AddSingleton(Gates);
            configureSilo?.Invoke(silo);
        });
    }

    public DurableTaskFaultInjector Injector { get; }
    public FaultTestGates Gates { get; }
    public DurableTaskRecoveryFixture Fixture { get; }
    public IClusterClient Client => Fixture.Client;

    public ValueTask InitializeAsync() => Fixture.InitializeAsync();

    public async ValueTask DisposeAsync()
    {
        Injector.Reset();
        await Fixture.DisposeAsync();
    }

    /// <summary>
    /// Schedules a root task and waits for its terminal response, retrying the client's calls which fail with an
    /// infrastructure failure and advancing the Durable Jobs clock while the task runs.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A client call which fails, for example because the grain's activation fails to read its journal or its write fails,
    /// is retried with the same task ID; the task ID deduplicates the schedule. Only the task's response is the outcome.
    /// </para>
    /// <para>
    /// When <paramref name="nudge"/> is provided and the task doesn't complete within <see cref="StallTimeout"/>, the harness
    /// reports the stall through <paramref name="onStalled"/>, calls <paramref name="nudge"/>, which activates the run's other
    /// grains, and keeps waiting, so a run whose only defect is a lost wake-up still has its outcome checked.
    /// </para>
    /// <para>
    /// A run which involves no delays or retries passes <paramref name="advanceClock"/> as <see langword="false"/>, so its
    /// Durable Jobs wake-ups follow real time.
    /// </para>
    /// </remarks>
    public async Task<DurableTaskResponse> RunAsync<T>(
        IAddressable grain,
        string rootId,
        Func<CancellationToken, Task> schedule,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task>? nudge = null,
        Action? onStalled = null,
        bool advanceClock = true)
    {
        var deadline = Stopwatch.StartNew();
        var clientFailures = new List<string>();
        await RetryAsync(schedule, clientFailures, deadline, cancellationToken);
        var reference = grain.GetDurableTask<T>(rootId);
        while (true)
        {
            if (nudge is not null && deadline.Elapsed > StallTimeout)
            {
                onStalled?.Invoke();
                await RetryAsync(nudge, clientFailures, deadline, cancellationToken);
                nudge = null;
            }

            try
            {
                var response = await reference.GetResponseAsync(Poll, cancellationToken);
                if (response.IsCompleted)
                {
                    return response;
                }
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                clientFailures.Add(exception.GetType().Name + ": " + exception.Message);
            }

            ThrowIfExpired(deadline, $"task '{rootId}' on {grain.GetGrainId()} to complete", clientFailures);
            if (advanceClock)
            {
                Fixture.JobClock.Advance(ClockStep);
            }
        }
    }

    /// <summary>
    /// Waits until the counted operations, such as a run's journal writes, stop: none occurs within <see cref="QuietPeriod"/>
    /// after the harness advances the Durable Jobs clock, so retries which are due run first.
    /// </summary>
    public async Task SettleAsync(IReadOnlyList<InjectedFault> counters, CancellationToken cancellationToken)
    {
        var deadline = Stopwatch.StartNew();
        while (true)
        {
            var next = counters.Select(static counter => counter.WaitForMatchesAsync(counter.Matches + 1)).ToList();
            Fixture.JobClock.Advance(ClockStep);
            var quiet = Task.Delay(QuietPeriod, cancellationToken);
            if (await Task.WhenAny([quiet, .. next]) == quiet)
            {
                return;
            }

            ThrowIfExpired(deadline, "the run's journal writes to stop", []);
        }
    }

    /// <summary>Calls a grain until the call succeeds, retrying infrastructure failures.</summary>
    public async Task<TResult> CallAsync<TResult>(Func<CancellationToken, Task<TResult>> call, CancellationToken cancellationToken)
    {
        var deadline = Stopwatch.StartNew();
        var failures = new List<string>();
        TResult result = default!;
        await RetryAsync(async token => result = await call(token), failures, deadline, cancellationToken);
        return result;
    }

    private async Task RetryAsync(
        Func<CancellationToken, Task> call,
        List<string> failures,
        Stopwatch deadline,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            try
            {
                await call(cancellationToken);
                return;
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested && exception is not DurableTaskConflictException)
            {
                failures.Add(exception.GetType().Name + ": " + exception.Message);
            }

            ThrowIfExpired(deadline, "a client call to succeed", failures);
            Fixture.JobClock.Advance(ClockStep);
            await Task.Delay(10, cancellationToken);
        }
    }

    private static void ThrowIfExpired(Stopwatch deadline, string waitingFor, List<string> failures)
    {
        if (deadline.Elapsed > RunTimeout)
        {
            throw new TimeoutException(
                $"Timed out after {RunTimeout} waiting for {waitingFor}. Client call failures ({failures.Count}): "
                + string.Join(" | ", failures.Distinct().Take(5)));
        }
    }
}
