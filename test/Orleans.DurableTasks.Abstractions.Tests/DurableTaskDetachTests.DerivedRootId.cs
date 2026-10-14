using Orleans.DurableTasks;
using Xunit;

namespace Orleans.DurableTasks.Abstractions.Tests;

public partial class DurableTaskDetachTests
{
    [Fact]
    public void DerivedRootIdAppendsTheSegmentToTheStartersId()
    {
        Assert.Equal("order-42:release", DetachedTaskId.For(TaskId.CreateRoot("order-42"), "release"));
        Assert.Equal("order-42/charge:release", DetachedTaskId.For(TaskId.Parse("order-42/charge"), "release"));

        // A detached task derives identifiers for the tasks it detaches in turn.
        Assert.Equal("order-42:release:audit", DetachedTaskId.For(TaskId.CreateRoot("order-42:release"), "audit"));
        Assert.NotEqual(
            DetachedTaskId.For(TaskId.CreateRoot("a"), "b"),
            DetachedTaskId.For(TaskId.Parse("a/b"), "b"));
    }

    [Fact]
    public void DerivedRootIdValidatesItsArguments()
    {
        var starter = TaskId.CreateRoot("starter");

        Assert.Throws<ArgumentException>(() => DetachedTaskId.For(TaskId.None, "release"));
        Assert.Throws<ArgumentNullException>(() => DetachedTaskId.For(starter, null!));
        Assert.Throws<ArgumentException>(() => DetachedTaskId.For(starter, string.Empty));

        // A segment containing the separator could alias another starter's derivation, such as starter "a:b" and "c".
        Assert.Throws<ArgumentException>(() => DetachedTaskId.For(TaskId.CreateRoot("a"), "b:c"));
    }

    [Fact]
    public void DerivedRootIdOutsideAnExecutionRequiresAnExplicitStarter()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => DetachedTaskId.For("release"));

        Assert.Contains("durable execution context", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DetachInExecutionUsesTheRootIdDerivedFromTheExecution()
    {
        var definition = new DetachableRootDefinition();
        var context = new DetachRecordingContext(TaskId.Parse("order-42/charge"));
        var first = await RunAsync(context, async () => await definition.DetachAsync(DetachedTaskId.For("release")));
        var replay = await RunAsync(
            new DetachRecordingContext(TaskId.Parse("order-42/charge")),
            async () => await definition.DetachAsync(DetachedTaskId.For("release")));

        Assert.Equal(DurableTaskStatus.CompletedSuccessfully, first.Response.Status);
        var entry = Assert.Single(context.Detached);
        Assert.Equal(TaskId.CreateRoot("order-42/charge:release"), entry.RootId);
        Assert.Equal(TaskId.None, entry.RootId.Parent());
        Assert.Equal(entry.RootId, Assert.Single(replay.Context.Detached).RootId);
    }
}
