using Orleans.DurableTasks;
using Xunit;

namespace Orleans.DurableTasks.Abstractions.Tests;

/// <summary>Tests the replay contract of <see cref="DurableTask.RunInline{TResult}(string, Func{TResult})"/>.</summary>
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableTasks")]
[TestCategory("BVT")]
public class DurableTaskRunInlineTests
{
    private static readonly DateTimeOffset Now = new(2030, 1, 2, 3, 4, 5, TimeSpan.Zero);

    [Fact]
    public async Task RunInlineRunsItsEffectOnceAndEveryRunReturnsItsRecordedResult()
    {
        var host = new TestHost(Now);
        var taskId = TaskId.CreateRoot("root");
        var runs = 0;

        Guid Body() => DurableTask.RunInline("id", () =>
        {
            runs++;
            return Guid.NewGuid();
        });

        var first = await RunAsync(host, taskId, Body);
        var replayed = await RunAsync(host, taskId, Body);

        Assert.Equal(1, runs);
        Assert.Equal(first, replayed);
        Assert.Equal([TaskId.Parse("root/id")], host.Effects.Keys);
    }

    [Fact]
    public async Task RepeatedNamesIdentifyLaterOccurrencesByOrder()
    {
        var host = new TestHost(Now);
        var taskId = TaskId.CreateRoot("root");
        var runs = 0;

        int[] Body()
        {
            var values = new int[3];
            for (var i = 0; i < values.Length; i++)
            {
                var value = i * 10;
                values[i] = DurableTask.RunInline("line", () =>
                {
                    runs++;
                    return value;
                });
            }

            return values;
        }

        var first = await RunAsync(host, taskId, Body);
        var replayed = await RunAsync(host, taskId, Body);

        Assert.Equal(new[] { 0, 10, 20 }, first);
        Assert.Equal(new[] { 0, 10, 20 }, replayed);
        Assert.Equal(3, runs);
        Assert.Equal(
            ["root/line", "root/line$2", "root/line$3"],
            host.Effects.Keys.Select(id => id.ToString()).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task RunInlineRecordsTheExceptionItsEffectThrows()
    {
        var host = new TestHost(Now);
        var taskId = TaskId.CreateRoot("root");
        var runs = 0;

        int Fail()
        {
            runs++;
            throw new InvalidOperationException("Recorded failure.");
        }

        string Body()
        {
            try
            {
                DurableTask.RunInline("fail", Fail);
                return "completed";
            }
            catch (InvalidOperationException exception)
            {
                return exception.Message;
            }
        }

        Assert.Equal("Recorded failure.", await RunAsync(host, taskId, Body));
        Assert.Equal("Recorded failure.", await RunAsync(host, taskId, Body));
        Assert.Equal(1, runs);
    }

    [Fact]
    public async Task ActionEffectRunsOnce()
    {
        var host = new TestHost(Now);
        var taskId = TaskId.CreateRoot("root");
        var runs = 0;

        bool Body()
        {
            DurableTask.RunInline("mark", () => runs++);
            return true;
        }

        await RunAsync(host, taskId, Body);
        await RunAsync(host, taskId, Body);

        Assert.Equal(1, runs);
    }

    [Fact]
    public void OutsideADurableTaskTheEffectRunsEveryTime()
    {
        var runs = 0;

        Assert.Equal(1, DurableTask.RunInline("count", () => ++runs));
        Assert.Equal(2, DurableTask.RunInline("count", () => ++runs));
        DurableTask.RunInline("count", () => { runs++; });
        Assert.Equal(3, runs);
        Assert.Throws<InvalidOperationException>(() => DurableTask.RunInline("fail", Fail));

        static int Fail() => throw new InvalidOperationException();
    }

    [Theory]
    [InlineData("")]
    [InlineData("$reserved")]
    [InlineData("line$2")]
    public void NamesAreNonEmptyAndExcludeTheGeneratedIdentifierMarker(string name)
    {
        Assert.Throws<ArgumentException>(() => DurableTask.RunInline(name, () => 1));
    }

    [Fact]
    public async Task NamesAndEffectsAreValidatedBeforeTheEffectRuns()
    {
        var host = new TestHost(Now);
        var runs = 0;

        var response = await DurableTaskRuntimeHelper.RunAsync(
            DurableTask.Run(_ => DurableTask.RunInline(null!, () => ++runs)),
            host.CreateContext(TaskId.CreateRoot("root")));

        Assert.IsType<ArgumentNullException>(response.Exception);
        Assert.Throws<ArgumentNullException>(() => DurableTask.RunInline("step", (Func<int>)null!));
        Assert.Throws<ArgumentNullException>(() => DurableTask.RunInline("step", (Action)null!));
        Assert.Equal(0, runs);
        Assert.Empty(host.Effects);
    }

    [Fact]
    public async Task EffectWhichReturnsAnAsynchronousOperationIsRejectedBeforeItRuns()
    {
        var host = new TestHost(Now);
        var runs = 0;

        // Asynchronous delegates don't compile; a delegate whose result type is asynchronous through generic code is
        // rejected when it runs, inside and outside a durable task.
        var response = await DurableTaskRuntimeHelper.RunAsync(
            DurableTask.Run(_ => RunInline<ValueTask<int>>(() => ValueTask.FromResult(++runs))),
            host.CreateContext(TaskId.CreateRoot("root")));

        Assert.IsType<NotSupportedException>(response.Exception);
        Assert.Throws<NotSupportedException>(() => RunInline<ValueTask>(() => new ValueTask(Task.FromResult(++runs))));
        Assert.Throws<NotSupportedException>(() => { _ = RunInline<Task<int>>(() => Task.FromResult(++runs)); });
        Assert.Throws<NotSupportedException>(() => RunInline<DurableTask<int>>(() => DurableTask.FromResult(++runs)));
        Assert.Equal(0, runs);
        Assert.Empty(host.Effects);

        static T RunInline<T>(Func<T> effect) => DurableTask.RunInline("generic", effect);
    }

    [Fact]
    public async Task EffectRecordsItsOutcomeInTheWorkWhichRunsIt()
    {
        var log = new List<string>();
        var context = new EffectContext(TaskId.CreateRoot("root"), log);

        var response = await DurableTaskRuntimeHelper.RunAsync(Body(), context);

        Assert.Equal(DurableTaskStatus.CompletedSuccessfully, response.Status);
        Assert.Equal(["effect", "record root/step", "after"], log);

        async DurableTask Body()
        {
            DurableTask.RunInline("step", () => log.Add("effect"));
            log.Add("after");
            await Task.CompletedTask;
        }
    }

    [Fact]
    public async Task NamedChildrenCombinatorsAndEffectsShareOneCountPerName()
    {
        var host = new TestHost(Now);

        var response = await DurableTaskRuntimeHelper.RunAsync(Body(), host.CreateContext(TaskId.CreateRoot("root")));

        // An occurrence of a name has the same identifier whichever kind of operation uses it, so a host can detect a
        // deployment which changes the kind of an occurrence.
        Assert.Equal(DurableTaskStatus.CompletedSuccessfully, response.Status);
        Assert.Equal(
            ["root/step", "root/step$4"],
            host.Effects.Keys.Select(id => id.ToString()).Order(StringComparer.Ordinal));
        Assert.Contains(TaskId.Parse("root/step$2"), host.EntryIds);
        Assert.Contains(TaskId.Parse("root/step$3/0"), host.EntryIds);

        static async DurableTask Body()
        {
            DurableTask.RunInline("step", () => 1);
            await DurableTask.Run(static _ => { }).WithId("step");
            await DurableTask.WhenAll([DurableTask.Run(static _ => { })]).WithId("step");
            DurableTask.RunInline("step", () => 2);
        }
    }

    [Fact]
    public async Task RunInlineAndAStepWithTheSameNameUseOneIdentifier()
    {
        var onceHost = new TestHost(Now);
        var stepHost = new TestHost(Now);

        var once = await DurableTaskRuntimeHelper.RunAsync(
            DurableTask.Run(_ => DurableTask.RunInline("get-id", Guid.NewGuid)),
            onceHost.CreateContext(TaskId.CreateRoot("root")));
        var step = await DurableTaskRuntimeHelper.RunAsync(StepBody(), stepHost.CreateContext(TaskId.CreateRoot("root")));

        Assert.Equal(DurableTaskStatus.CompletedSuccessfully, once.Status);
        Assert.Equal(DurableTaskStatus.CompletedSuccessfully, step.Status);
        Assert.Equal([TaskId.Parse("root/get-id")], onceHost.Effects.Keys);
        Assert.Contains(TaskId.Parse("root/get-id"), stepHost.EntryIds);

        static async DurableTask<Guid> StepBody() => await DurableTask.Run(async () => Guid.NewGuid()).WithId("get-id");
    }

    [Fact]
    public async Task HostWithoutRecordedEffectsRejectsRunInline()
    {
        var context = new EffectContext(TaskId.CreateRoot("root"), log: null);
        var runs = 0;

        var response = await DurableTaskRuntimeHelper.RunAsync(DurableTask.Run(_ => DurableTask.RunInline("id", () => ++runs)), context);

        Assert.IsType<NotSupportedException>(response.Exception);
        Assert.Equal(0, runs);
    }

    [Fact]
    public async Task UnnamedRunInlineRunsItsEffectOnceUnderAGeneratedIdentifier()
    {
        var host = new TestHost(Now);
        var taskId = TaskId.CreateRoot("root");
        var runs = 0;

        Guid Body() => DurableTask.RunInline(() =>
        {
            runs++;
            return Guid.NewGuid();
        });

        var first = await RunAsync(host, taskId, Body);
        var replayed = await RunAsync(host, taskId, Body);

        Assert.Equal(1, runs);
        Assert.Equal(first, replayed);
        Assert.Equal([TaskId.Parse("root/$child-1")], host.Effects.Keys);
    }

    [Fact]
    public async Task UnnamedEffectsAndUnnamedChildrenShareGeneratedIdentifiersInOrder()
    {
        var host = new TestHost(Now);
        var effectRuns = 0;
        var childRuns = 0;

        var first = await DurableTaskRuntimeHelper.RunAsync(Body(), host.CreateContext(TaskId.CreateRoot("root")));
        var replayed = await DurableTaskRuntimeHelper.RunAsync(Body(), host.CreateContext(TaskId.CreateRoot("root")));

        // Named operations don't advance the generated count, so the unnamed operations are $child-1, $child-2, and
        // $child-3 in the order in which the body creates them, and each replay finds the same records.
        Assert.Equal([1, 2, 3], first.GetResult<int[]>());
        Assert.Equal([1, 2, 3], replayed.GetResult<int[]>());
        Assert.Equal(2, effectRuns);
        Assert.Equal(1, childRuns);
        Assert.Equal(
            ["root/$child-1", "root/$child-3", "root/named"],
            host.Effects.Keys.Select(id => id.ToString()).Order(StringComparer.Ordinal));
        Assert.Contains(TaskId.Parse("root/$child-2"), host.EntryIds);

        async DurableTask<int[]> Body()
        {
            var a = DurableTask.RunInline(() => ++effectRuns);
            DurableTask.RunInline("named", () => 0);
            var b = await DurableTask.Run(_ => 1 + ++childRuns);
            var c = DurableTask.RunInline(() => 1 + ++effectRuns);
            return [a, b, c];
        }
    }

    [Fact]
    public async Task UnnamedActionEffectRunsOnceAndRecordsItsFailure()
    {
        var host = new TestHost(Now);
        var taskId = TaskId.CreateRoot("root");
        var runs = 0;

        string Body()
        {
            DurableTask.RunInline(() => { runs++; });
            try
            {
                DurableTask.RunInline(Fail);
                return "completed";
            }
            catch (InvalidOperationException exception)
            {
                return exception.Message;
            }
        }

        void Fail()
        {
            runs++;
            throw new InvalidOperationException("Recorded failure.");
        }

        Assert.Equal("Recorded failure.", await RunAsync(host, taskId, Body));
        Assert.Equal("Recorded failure.", await RunAsync(host, taskId, Body));
        Assert.Equal(2, runs);
        Assert.Equal(
            ["root/$child-1", "root/$child-2"],
            host.Effects.Keys.Select(id => id.ToString()).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void OutsideADurableTaskAnUnnamedEffectRunsEveryTime()
    {
        var runs = 0;

        Assert.Equal(1, DurableTask.RunInline(() => ++runs));
        Assert.Equal(2, DurableTask.RunInline(() => ++runs));
        DurableTask.RunInline(() => { runs++; });
        Assert.Equal(3, runs);
        Assert.Throws<InvalidOperationException>(() => DurableTask.RunInline(Fail));

        static int Fail() => throw new InvalidOperationException();
    }

    [Fact]
    public async Task UnnamedEffectsAreValidatedBeforeTheyRun()
    {
        var host = new TestHost(Now);
        var runs = 0;

        var response = await DurableTaskRuntimeHelper.RunAsync(
            DurableTask.Run(_ => RunInline<ValueTask<int>>(() => ValueTask.FromResult(++runs))),
            host.CreateContext(TaskId.CreateRoot("root")));

        Assert.IsType<NotSupportedException>(response.Exception);
        Assert.Throws<ArgumentNullException>(() => DurableTask.RunInline((Func<int>)null!));
        Assert.Throws<ArgumentNullException>(() => DurableTask.RunInline((Action)null!));
        Assert.Throws<NotSupportedException>(() => RunInline<ValueTask>(() => new ValueTask(Task.FromResult(++runs))));
        Assert.Throws<NotSupportedException>(() => { _ = RunInline<Task<int>>(() => Task.FromResult(++runs)); });
        Assert.Throws<NotSupportedException>(() => RunInline<DurableTask<int>>(() => DurableTask.FromResult(++runs)));
        Assert.Equal(0, runs);
        Assert.Empty(host.Effects);

        static T RunInline<T>(Func<T> effect) => DurableTask.RunInline(effect);
    }

    [Fact]
    public async Task HostWithoutRecordedEffectsRejectsUnnamedRunInline()
    {
        var context = new EffectContext(TaskId.CreateRoot("root"), log: null);
        var runs = 0;

        var response = await DurableTaskRuntimeHelper.RunAsync(DurableTask.Run(_ => DurableTask.RunInline(() => ++runs)), context);

        Assert.IsType<NotSupportedException>(response.Exception);
        Assert.Equal(0, runs);
    }

    private static async Task<T> RunAsync<T>(TestHost host, TaskId taskId, Func<T> body)
    {
        var response = await DurableTaskRuntimeHelper.RunAsync(DurableTask.Run(_ => body()), host.CreateContext(taskId));
        return response.GetResult<T>();
    }

    /// <summary>A host which runs effects and logs their records, or which doesn't support them.</summary>
    private sealed class EffectContext(TaskId taskId, List<string>? log) : DurableTaskContext(taskId)
    {
        public override DateTimeOffset UtcNow => Now;

        protected override DurableTaskResponse RunEffect<TState>(TaskId effectId, TState state, Func<TState, DurableTaskResponse> effect)
        {
            if (log is null)
            {
                return base.RunEffect(effectId, state, effect);
            }

            var outcome = effect(state);
            log.Add($"record {effectId}");
            return outcome;
        }

        protected override ValueTask<IScheduledTaskHandle> StartTaskAsync(TaskId taskId, DurableTask taskDefinition, DurableTaskStartOptions options, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        protected override ValueTask<DurableTaskResponse> ScheduleDelayAsync(TaskId taskId, DateTimeOffset dueTime, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        protected override IScheduledTaskHandle GetChildTaskHandle(TaskId taskId) => throw new NotSupportedException();

        protected override ValueTask<TaskId> SelectCompletionAsync(TaskId decisionId, IReadOnlyList<TaskId> candidates, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }
}
