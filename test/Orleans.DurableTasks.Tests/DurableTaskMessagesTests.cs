#nullable enable
using System;
using System.Distributed.DurableTasks;
using NSubstitute;
using Orleans.DurableTasks;
using Xunit;

namespace Orleans.DurableTasks.Tests;

[TestCategory("BVT")]
public class DurableTaskMessagesTests
{
    [Fact]
    public void DurableTaskInvocationMessage_ExposesTaskIdAndRequestUnchanged()
    {
        var taskId = TaskId.Create("invocation-task");
        var request = Substitute.For<IDurableTaskRequest>();

        var message = new DurableTaskInvocationMessage
        {
            TaskId = taskId,
            Request = request,
        };

        Assert.Equal(taskId, message.TaskId);
        Assert.Same(request, message.Request);
    }

    [Fact]
    public void DurableTaskCompletionMessage_ExposesTaskIdAndResponseUnchanged()
    {
        var taskId = TaskId.Create("completion-task");
        var response = DurableTaskResponse.Completed;

        var message = new DurableTaskCompletionMessage
        {
            TaskId = taskId,
            Response = response,
        };

        Assert.Equal(taskId, message.TaskId);
        Assert.Same(response, message.Response);
    }

    [Fact]
    public void DurableTaskCancellationMessage_ExposesTaskIdUnchanged()
    {
        var taskId = TaskId.Create("cancellation-task");

        var message = new DurableTaskCancellationMessage
        {
            TaskId = taskId,
        };

        Assert.Equal(taskId, message.TaskId);
        Assert.NotEqual(TaskId.Create("some-other-task"), message.TaskId);
    }

    [Fact]
    public void DurableTaskResumeMessage_ExposesTaskIdUnchanged()
    {
        var taskId = TaskId.Create("resume-task");

        var message = new DurableTaskResumeMessage
        {
            TaskId = taskId,
        };

        Assert.Equal(taskId, message.TaskId);
        Assert.NotEqual(TaskId.Create("some-other-task"), message.TaskId);
    }

    [Fact]
    public void DurableTaskCompletionMessage_DistinctResponses_AreNotConfusedWithEachOther()
    {
        var successMessage = new DurableTaskCompletionMessage
        {
            TaskId = TaskId.Create("t1"),
            Response = DurableTaskResponse.Completed,
        };
        var failureMessage = new DurableTaskCompletionMessage
        {
            TaskId = TaskId.Create("t1"),
            Response = DurableTaskResponse.FromException(new InvalidOperationException("boom")),
        };

        Assert.NotSame(successMessage.Response, failureMessage.Response);
        Assert.True(successMessage.Response.IsCompleted);
        Assert.True(failureMessage.Response.IsCompleted);
        Assert.False(successMessage.Response.Status == DurableTaskStatus.Failed);
    }
}
