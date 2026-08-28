using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using Orleans.Dashboard.Implementation;
using Xunit;

namespace UnitTests.Implementation;

[TestCategory("BVT")]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Dashboard")]
public class DashboardLoggerTests
{
    [Fact]
    public void Log_ActiveSubscriber_ReceivesExactLevelEventAndFormattedMessage()
    {
        var logger = new DashboardLogger();
        var callbacks = new List<(EventId EventId, LogLevel Level, string Message)>();
        var expectedException = new InvalidOperationException("activation failed");
        Exception? formatterException = null;
        string? formatterState = null;
        var formatterCalls = 0;
        logger.Add((eventId, level, message) => callbacks.Add((eventId, level, message)));

        logger.Log(
            LogLevel.Warning,
            new EventId(42, "DashboardActivation"),
            "Silo A",
            expectedException,
            (state, exception) =>
            {
                formatterCalls++;
                formatterState = state;
                formatterException = exception;
                return $"{state}: {exception!.Message}";
            });

        var callback = Assert.Single(callbacks);
        Assert.Equal(new EventId(42, "DashboardActivation"), callback.EventId);
        Assert.Equal(LogLevel.Warning, callback.Level);
        Assert.Equal("Silo A: activation failed", callback.Message);
        Assert.Equal(1, formatterCalls);
        Assert.Equal("Silo A", formatterState);
        Assert.Same(expectedException, formatterException);
    }

    [Fact]
    public void Log_MultipleSubscribers_NotifiesEachSubscriberOnce()
    {
        var logger = new DashboardLogger();
        var first = new List<string>();
        var second = new List<string>();
        var formatterCalls = 0;
        logger.Add((_, _, message) => first.Add(message));
        logger.Add((_, _, message) => second.Add(message));

        logger.Log(LogLevel.Information, new EventId(7, "Refresh"), 12, null, (state, _) =>
        {
            formatterCalls++;
            return $"count={state}";
        });

        Assert.Equal(["count=12"], first);
        Assert.Equal(["count=12"], second);
        Assert.Equal(1, formatterCalls);
    }

    [Fact]
    public void Remove_RemovedSubscriberDoesNotReceiveSubsequentLogs()
    {
        var logger = new DashboardLogger();
        var removedMessages = new List<string>();
        var retainedMessages = new List<string>();
        Action<EventId, LogLevel, string> removed = (_, _, message) => removedMessages.Add(message);
        logger.Add(removed);
        logger.Add((_, _, message) => retainedMessages.Add(message));
        logger.Log(LogLevel.Debug, new EventId(1), "before", null, static (state, _) => state);

        logger.Remove(removed);
        logger.Log(LogLevel.Error, new EventId(2), "after", null, static (state, _) => state);

        Assert.Equal(["before"], removedMessages);
        Assert.Equal(["before", "after"], retainedMessages);
    }

    [Fact]
    public void Log_NoSubscribers_DoesNotFormatAndProviderRemainsEnabled()
    {
        var logger = new DashboardLogger();
        var formatterCalls = 0;

        logger.Log(
            LogLevel.None,
            new EventId(99),
            string.Empty,
            null,
            (state, _) =>
            {
                formatterCalls++;
                return state;
            });

        Assert.Equal(0, formatterCalls);
        Assert.True(logger.IsEnabled(LogLevel.None));
        Assert.True(logger.IsEnabled(LogLevel.Trace));
        Assert.Same(logger, logger.CreateLogger("ignored.category"));
    }

    [Fact]
    public void BeginScope_DisposeIsSafeAndDoesNotPublishTrace()
    {
        var logger = new DashboardLogger();
        var callbackCount = 0;
        logger.Add((_, _, _) => callbackCount++);

        var firstScope = logger.BeginScope("first");
        var secondScope = logger.BeginScope("second");

        Assert.NotNull(firstScope);
        Assert.Same(firstScope, secondScope);
        firstScope.Dispose();
        secondScope!.Dispose();
        Assert.Equal(0, callbackCount);
    }
}
