using System.Reflection;
using Orleans.DurableTasks;
using Xunit;

namespace Orleans.DurableTasks.Abstractions.Tests;

/// <summary>
/// Pins the wire names of the abstraction types which hosts persist, such as recorded responses in a journal, so a rename
/// of a type leaves recorded data readable.
/// </summary>
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableTasks")]
[TestCategory("BVT")]
public class PersistedTypeAliasTests
{
    public static TheoryData<Type, string> PersistedTypes() => new()
    {
        { typeof(TaskId), "Orleans.DurableTasks.TaskId" },
        { typeof(DurableTask), "Orleans.DurableTasks.DurableTask" },
        { typeof(DurableTask<>), "Orleans.DurableTasks.DurableTask`1" },
        { typeof(DurableTaskStatus), "Orleans.DurableTasks.DurableTaskStatus" },
        { typeof(DurableTaskResponseKind), "Orleans.DurableTasks.DurableTaskResponseKind" },
        { typeof(DurableTaskResponse), "Orleans.DurableTasks.DurableTaskResponse" },
        { typeof(SuccessDurableTaskResponse), "Orleans.DurableTasks.SuccessDurableTaskResponse" },
        { typeof(DurableTaskResponse<>), "Orleans.DurableTasks.DurableTaskResponse`1" },
        { typeof(ExceptionDurableTaskResponse), "Orleans.DurableTasks.ExceptionDurableTaskResponse" },
        { typeof(CanceledDurableTaskResponse), "Orleans.DurableTasks.CanceledDurableTaskResponse" },
        { typeof(PendingDurableTaskResponse), "Orleans.DurableTasks.PendingDurableTaskResponse" },
        { typeof(SubscribedDurableTaskResponse), "Orleans.DurableTasks.SubscribedDurableTaskResponse" },
        { typeof(DurableTaskStartOptions), "Orleans.DurableTasks.DurableTaskStartOptions" },
        { typeof(PollingOptions), "Orleans.DurableTasks.PollingOptions" },
        { typeof(DurableTaskAttemptCancellationReason), "Orleans.DurableTasks.DurableTaskAttemptCancellationReason" },
        { typeof(DurableTaskAbortedException), "Orleans.DurableTasks.DurableTaskAbortedException" },
        { typeof(DurableTaskAttemptCanceledException), "Orleans.DurableTasks.DurableTaskAttemptCanceledException" },
        { typeof(DurableTaskVersionException), "Orleans.DurableTasks.DurableTaskVersionException" },
    };

    [Theory]
    [MemberData(nameof(PersistedTypes))]
    public void PersistedTypesKeepTheirAliases(Type type, string alias)
        => Assert.Equal(alias, Assert.Single(type.GetCustomAttributes<AliasAttribute>(inherit: false)).Alias);

    [Fact]
    public void EveryAliasedTypeIsPinned()
    {
        var pinned = PersistedTypes().Select(static row => row.Data.Item1).ToHashSet();
        var aliased = typeof(DurableTask).Assembly.GetTypes().Where(static type => type.IsDefined(typeof(AliasAttribute), inherit: false));

        Assert.All(aliased, type => Assert.Contains(type, pinned));
    }
}
