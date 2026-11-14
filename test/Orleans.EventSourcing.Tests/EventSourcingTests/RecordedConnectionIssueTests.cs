using Orleans.EventSourcing;
using Orleans.EventSourcing.Common;
using Xunit;

namespace Tester.EventSourcingTests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestCategory("BVT")]
[TestArea("EventSourcing")]
public sealed class RecordedConnectionIssueTests
{
    [Theory]
    [InlineData("newIssue")]
    [InlineData("listener")]
    [InlineData("services")]
    public void Record_NullArgument_PreservesExistingIssue(string parameter)
    {
        var recorded = new RecordedConnectionIssue();
        var listener = new RecordingLogViewAdaptorHost();
        var services = new RecordingProtocolServices();
        var original = new TestConnectionIssue();
        recorded.Record(original, listener, services);
        var next = new TestConnectionIssue();

        var exception = Assert.Throws<ArgumentNullException>(() => recorded.Record(
            parameter == "newIssue" ? null! : next,
            parameter == "listener" ? null! : listener,
            parameter == "services" ? null! : services));

        Assert.Equal(parameter, exception.ParamName);
        Assert.Same(original, recorded.Issue);
        Assert.Equal(default, next.TimeStamp);
        Assert.Equal(0, next.NumberOfConsecutiveFailures);
        Assert.Same(original, Assert.Single(listener.ConnectionIssues));
        Assert.Empty(services.UserCodeExceptions);
    }

    [Theory]
    [InlineData("listener", false)]
    [InlineData("listener", true)]
    [InlineData("services", false)]
    [InlineData("services", true)]
    public void Resolve_NullArgument_PreservesExistingIssue(string parameter, bool hasIssue)
    {
        var recorded = new RecordedConnectionIssue();
        var listener = new RecordingLogViewAdaptorHost();
        var services = new RecordingProtocolServices();
        var original = hasIssue ? new TestConnectionIssue() : null;
        if (original is not null)
        {
            recorded.Record(original, listener, services);
        }

        var exception = Assert.Throws<ArgumentNullException>(() => recorded.Resolve(
            parameter == "listener" ? null! : listener,
            parameter == "services" ? null! : services));

        Assert.Equal(parameter, exception.ParamName);
        Assert.Same(original, recorded.Issue);
        Assert.Empty(listener.ResolvedConnectionIssues);
        Assert.Empty(services.UserCodeExceptions);
    }

    [Fact]
    public void RecordAndResolve_PreserveHistoryAndListenerRetryDelay()
    {
        var recorded = new RecordedConnectionIssue();
        var services = new RecordingProtocolServices();
        var reported = new List<ConnectionIssue>();
        var resolved = new List<ConnectionIssue>();
        var listener = new CallbackListener(issue =>
        {
            reported.Add(issue);
            issue.RetryDelay += TimeSpan.FromSeconds(4);
        }, resolved.Add);
        var first = new TestConnectionIssue();
        var second = new TestConnectionIssue();

        recorded.Record(first, listener, services);
        recorded.Record(second, listener, services);

        Assert.Equal([first, second], reported);
        Assert.Equal(first.TimeStamp, first.TimeOfFirstFailure);
        Assert.Equal(first.TimeOfFirstFailure, second.TimeOfFirstFailure);
        Assert.Equal(1, first.NumberOfConsecutiveFailures);
        Assert.Equal(2, second.NumberOfConsecutiveFailures);
        Assert.Equal(TimeSpan.FromSeconds(5), first.RetryDelay);
        Assert.Equal(TimeSpan.FromSeconds(10), second.RetryDelay);
        Assert.Same(second, recorded.Issue);

        recorded.Resolve(listener, services);
        recorded.Resolve(listener, services);

        Assert.Null(recorded.Issue);
        Assert.Same(second, Assert.Single(resolved));

        var third = new TestConnectionIssue();
        recorded.Record(third, listener, services);
        Assert.Equal(1, third.NumberOfConsecutiveFailures);
        Assert.Equal(third.TimeStamp, third.TimeOfFirstFailure);
        Assert.Equal(TimeSpan.FromSeconds(5), third.RetryDelay);
        Assert.Empty(services.UserCodeExceptions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NotificationFailure_IsReportedAndBookkeepingCompletes(bool resolving)
    {
        var recorded = new RecordedConnectionIssue();
        var services = new RecordingProtocolServices();
        var failure = new InvalidOperationException("listener failed");
        var issue = new TestConnectionIssue();
        var listener = new CallbackListener(_ => throw failure, _ => throw failure);
        if (resolving)
        {
            recorded.Record(issue, new RecordingLogViewAdaptorHost(), services);
            recorded.Resolve(listener, services);
            Assert.Null(recorded.Issue);
        }
        else
        {
            recorded.Record(issue, listener, services);
            Assert.Same(issue, recorded.Issue);
            Assert.Equal(1, issue.NumberOfConsecutiveFailures);
        }

        var reported = Assert.Single(services.UserCodeExceptions);
        Assert.Same(failure, reported.Exception);
        Assert.Equal(resolving ? nameof(IConnectionIssueListener.OnConnectionIssueResolved) : nameof(IConnectionIssueListener.OnConnectionIssue), reported.Callback);
        Assert.Equal(resolving ? nameof(RecordedConnectionIssue.Resolve) : nameof(RecordedConnectionIssue.Record), reported.Where);
    }

    private sealed class TestConnectionIssue : ConnectionIssue
    {
        public override TimeSpan ComputeRetryDelay(TimeSpan? previous) => (previous ?? TimeSpan.Zero) + TimeSpan.FromSeconds(1);
    }

    private sealed class CallbackListener(
        Action<ConnectionIssue> onIssue,
        Action<ConnectionIssue> onResolved) : IConnectionIssueListener
    {
        public void OnConnectionIssue(ConnectionIssue connectionIssue) => onIssue(connectionIssue);

        public void OnConnectionIssueResolved(ConnectionIssue connectionIssue) => onResolved(connectionIssue);
    }
}
