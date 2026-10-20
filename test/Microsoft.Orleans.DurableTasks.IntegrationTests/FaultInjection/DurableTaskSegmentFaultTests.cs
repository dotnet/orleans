using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;
using Xunit;

namespace Microsoft.Orleans.DurableTasks.IntegrationTests.FaultInjection;

/// <summary>
/// Ends a grain's activation at the segments of its durable task attempts, the code between their durable waits which runs
/// as a local request on the activation, and checks that each workflow still completes with its fault-free result, applies
/// each effect exactly once, and leaves no dead letters.
/// </summary>
/// <remarks>
/// <para>
/// A deactivation which the activation requests as it admits a segment stops the activation while the segment's code runs:
/// shutdown cancels the attempt, the segment ends, and its result is discarded. A deactivation which it requests once a
/// segment ends stops the activation while the attempt is parked between segments, which shutdown discards without
/// resuming it, or after the attempt ended. Either way, the next activation replays the execution from its journal.
/// </para>
/// <para>
/// A crash of the activation's silo as a segment is admitted ends the activation before the segment's code runs, and one
/// once a segment ends ends it before another request runs; the grains recover on the remaining silo through their
/// messages and Durable Jobs wake-ups.
/// </para>
/// <para>
/// The deactivation sweeps end the activation at every segment of each grain in turn, like
/// <see cref="DurableTaskCrashExplorationTests"/> fails every journal write; the crash cases end it at one segment each,
/// since a crash removes a silo from the test's cluster. The default suite sweeps the pipeline, and
/// <see cref="DurableTaskSegmentSweepTests"/> sweeps the race.
/// </para>
/// </remarks>
public abstract class DurableTaskSegmentFaultTests(string journalFormatKey) : IAsyncLifetime
{
    // Durable Jobs adopts a crashed silo's shards, which hold the crashed activations' wake-ups, in its periodic shard check.
    private readonly DurableTaskFaultHarness _harness = new(
        journalFormatKey,
        silo => silo.Services.Configure<DurableJobsOptions>(options => options.ShardCheckInterval = TimeSpan.FromSeconds(1)));

    public ValueTask InitializeAsync() => _harness.InitializeAsync();
    public ValueTask DisposeAsync() => _harness.DisposeAsync();

    [Theory]
    [InlineData(FaultPoint.SegmentStartDeactivates)]
    [InlineData(FaultPoint.SegmentEndDeactivates)]
    public Task DeactivationAtEverySegmentOfThePipelineOnlyDelaysCompletion(FaultPoint point) =>
        SweepSegmentsAsync(_harness, FaultWorkflow.Pipeline, point);

    /// <summary>Deactivates each grain of <paramref name="workflow"/> at each of its segments in turn, and checks every run.</summary>
    internal static Task SweepSegmentsAsync(DurableTaskFaultHarness harness, FaultWorkflow workflow, FaultPoint point) =>
        DurableTaskCrashExplorationTests.SweepAsync(harness, workflow, point, "segment", static run => run.Segments);

    /// <summary>A silo crash before a segment's code runs, or once it parked the attempt, only delays completion.</summary>
    /// <remarks>
    /// The workflow's third segment follows its first durable waits, and the partner's second segment starts the race's
    /// hold step.
    /// </remarks>
    [Theory]
    [InlineData(FaultWorkflow.Pipeline, FaultPoint.SegmentStartCrashesSilo, FaultRole.Workflow, 2)]
    [InlineData(FaultWorkflow.Pipeline, FaultPoint.SegmentEndCrashesSilo, FaultRole.Workflow, 2)]
    [InlineData(FaultWorkflow.RaceAndCompensate, FaultPoint.SegmentStartCrashesSilo, FaultRole.Partner, 1)]
    public async Task SiloCrashAtASegmentOnlyDelaysCompletion(FaultWorkflow workflow, FaultPoint point, FaultRole role, int occurrence)
    {
        var silos = _harness.Fixture.Cluster.Silos.Count;

        var run = await FaultScenario.RunAsync(_harness, workflow, new FaultSpec(point, role, occurrence));

        Assert.True(run.Fired, "The fault didn't fire.");
        Assert.True(run.Violations.Count == 0, string.Join(Environment.NewLine, run.Violations));
        Assert.Equal(silos - 1, _harness.Fixture.Cluster.Silos.Count);
    }
}

/// <summary>
/// Sweeps every segment of the race whose loser compensates, deactivating its grains as each segment starts, while its code
/// runs, and once it ends, while its attempt is parked.
/// </summary>
public abstract class DurableTaskSegmentSweepTests(string journalFormatKey) : IAsyncLifetime
{
    private readonly DurableTaskFaultHarness _harness = new(journalFormatKey);

    public ValueTask InitializeAsync() => _harness.InitializeAsync();
    public ValueTask DisposeAsync() => _harness.DisposeAsync();

    [Theory]
    [InlineData(FaultPoint.SegmentStartDeactivates)]
    [InlineData(FaultPoint.SegmentEndDeactivates)]
    public Task DeactivationAtEverySegmentOfTheRaceOnlyDelaysCompletion(FaultPoint point) =>
        DurableTaskSegmentFaultTests.SweepSegmentsAsync(_harness, FaultWorkflow.RaceAndCompensate, point);
}

/// <summary>Runs durable task segment fault coverage with task state stored in the default JSON journal format.</summary>
[Collection(DurableTaskRecoveryCollection.Name)]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableTasks")]
public sealed class JsonDurableTaskSegmentFaultTests() : DurableTaskSegmentFaultTests(DurableTaskRecoveryFixture.Json);

/// <summary>Runs durable task segment fault coverage with task state stored in the Orleans binary journal format.</summary>
[Collection(DurableTaskRecoveryCollection.Name)]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableTasks")]
public sealed class OrleansBinaryDurableTaskSegmentFaultTests() : DurableTaskSegmentFaultTests(DurableTaskRecoveryFixture.OrleansBinary);

/// <summary>Runs the race's segment sweep with task state stored in the default JSON journal format.</summary>
[Collection(DurableTaskRecoveryCollection.Name)]
[TestSuite("SlowBVT")]
[TestProvider("None")]
[TestArea("DurableTasks")]
public sealed class JsonDurableTaskSegmentSweepTests() : DurableTaskSegmentSweepTests(DurableTaskRecoveryFixture.Json);

/// <summary>Runs the race's segment sweep with task state stored in the Orleans binary journal format.</summary>
[Collection(DurableTaskRecoveryCollection.Name)]
[TestSuite("SlowBVT")]
[TestProvider("None")]
[TestArea("DurableTasks")]
public sealed class OrleansBinaryDurableTaskSegmentSweepTests() : DurableTaskSegmentSweepTests(DurableTaskRecoveryFixture.OrleansBinary);
