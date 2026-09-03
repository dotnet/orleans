using Orleans.EventSourcing;
using Orleans.EventSourcing.Common;
using Orleans.EventSourcing.CustomStorage;
using Xunit;
using CustomStorageAdaptor = Orleans.EventSourcing.CustomStorage.CustomStorageAdaptor<Tester.EventSourcingTests.TestLogView, Tester.EventSourcingTests.TestLogEntry>;
using CustomUpdateNotification = Orleans.EventSourcing.CustomStorage.CustomStorageAdaptor<Tester.EventSourcingTests.TestLogView, Tester.EventSourcingTests.TestLogEntry>.UpdateNotificationMessage;

namespace Tester.EventSourcingTests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestCategory("BVT")]
[TestArea("EventSourcing")]
public sealed class CustomStorageLogViewAdaptorTests
{
    [Fact]
    public async Task WriteAsync_WhenExpectedVersionMatches_PersistsEntriesInOrder()
    {
        var (adaptor, host, _) = CreateAdaptor();

        var result = await TestPhase.Await(
            adaptor.TryAppendRange(Entries("one", "two")),
            "custom storage range to commit");
        await TestPhase.Await(adaptor.PostOnDeactivate(), "custom storage writer to become idle");

        Assert.True(result);
        Assert.Equal(1, host.ApplyCount);
        Assert.Equal(0, host.ReadCount);
        Assert.Equal(0, host.LastExpectedVersion);
        Assert.Equal(["one", "two"], host.LastUpdates);
        Assert.Equal(2, host.StoredVersion);
        Assert.Equal(["one", "two"], host.StoredState.Entries);
        Assert.Equal(2, adaptor.ConfirmedVersion);
        Assert.Equal(["one", "two"], adaptor.ConfirmedView.Entries);
        Assert.Empty(adaptor.UnconfirmedSuffix);
    }

    [Fact]
    public async Task WriteAsync_WhenExpectedVersionConflicts_RereadsAndRejectsConditionalRange()
    {
        var (adaptor, host, _) = CreateAdaptor();
        host.SetStoredState(["remote"], 1);

        var result = await TestPhase.Await(
            adaptor.TryAppendRange(Entries("conditional-one", "conditional-two")),
            "custom storage conflict to resolve");
        await TestPhase.Await(adaptor.PostOnDeactivate(), "custom storage conflict worker to become idle");

        Assert.False(result);
        Assert.Equal(1, host.ApplyCount);
        Assert.Equal(1, host.ReadCount);
        Assert.Equal(0, host.LastExpectedVersion);
        Assert.Equal(["conditional-one", "conditional-two"], host.LastUpdates);
        Assert.Equal(1, adaptor.ConfirmedVersion);
        Assert.Equal(["remote"], adaptor.ConfirmedView.Entries);
        Assert.Equal(["remote"], adaptor.TentativeView.Entries);
        Assert.Empty(adaptor.UnconfirmedSuffix);
        Assert.Empty(host.ConnectionIssues);
    }

    [Fact]
    public async Task WriteAsync_WhenForwardedStorageFailureOccurs_UnwrapsIssueAndRetriesWithoutDuplication()
    {
        var (adaptor, host, _) = CreateAdaptor();
        host.NextApplyBehavior = CustomStorageApplyBehavior.ThrowProtocolTransportException;

        var result = await TestPhase.Await(
            adaptor.TryAppend(new TestLogEntry("once")),
            "custom storage retry to commit");
        await TestPhase.Await(adaptor.PostOnDeactivate(), "custom storage retry worker to become idle");

        Assert.True(result);
        Assert.Equal(2, host.ApplyCount);
        Assert.Equal(1, host.ReadCount);
        Assert.Equal(["once"], host.StoredState.Entries);
        Assert.Equal(1, host.StoredVersion);
        var issue = Assert.IsType<CustomStorageAdaptor.UpdatePrimaryFailed>(Assert.Single(host.ConnectionIssues));
        var exception = Assert.IsType<InvalidOperationException>(issue.Exception);
        Assert.Equal("custom storage unavailable", exception.Message);
        Assert.Same(issue, Assert.Single(host.ResolvedConnectionIssues));
    }

    [Fact]
    public async Task WriteAsync_WhenCachedTransitionFails_RereadsPersistedStateAndReportsUserException()
    {
        var (adaptor, host, services) = CreateAdaptor();
        host.ThrowOnNextUpdate();

        var result = await TestPhase.Await(
            adaptor.TryAppend(new TestLogEntry("persisted")),
            "custom storage transition recovery to complete");
        await TestPhase.Await(adaptor.PostOnDeactivate(), "custom storage transition worker to become idle");

        Assert.True(result);
        Assert.Equal(1, host.ApplyCount);
        Assert.Equal(1, host.ReadCount);
        Assert.Equal(["persisted"], host.StoredState.Entries);
        Assert.Equal(["persisted"], adaptor.ConfirmedView.Entries);
        Assert.Equal(1, adaptor.ConfirmedVersion);
        var exception = Assert.Single(services.UserCodeExceptions);
        Assert.Equal(("UpdateView", "WriteAsync", "view update failed"), (
            exception.Callback,
            exception.Where,
            exception.Exception.Message));
        Assert.Empty(host.ConnectionIssues);
    }

    [Fact]
    public async Task OnNotificationReceived_WithFutureUpdate_QueuesUntilPredecessorAndAppliesInOrder()
    {
        var (adaptor, host, _) = CreateAdaptor();

        await adaptor.OnProtocolMessageReceived(Notification(2, "two"));
        await TestPhase.Await(adaptor.PostOnDeactivate(), "future custom notification to drain");

        Assert.Equal(0, adaptor.ConfirmedVersion);
        Assert.Empty(adaptor.ConfirmedView.Entries);
        Assert.Empty(host.ViewChanges);

        await adaptor.OnProtocolMessageReceived(Notification(1, "one"));
        await TestPhase.Await(adaptor.PostOnDeactivate(), "contiguous custom notifications to drain");

        Assert.Equal(2, adaptor.ConfirmedVersion);
        Assert.Equal(["one", "two"], adaptor.ConfirmedView.Entries);
        Assert.Equal([(true, true)], host.ViewChanges);
    }

    [Fact]
    public async Task OnNotificationReceived_WithStaleUpdate_DiscardsWithoutChangingState()
    {
        var (adaptor, host, _) = CreateAdaptor();
        await adaptor.OnProtocolMessageReceived(Notification(2, "one", "two"));
        await TestPhase.Await(adaptor.PostOnDeactivate(), "initial custom notification to drain");
        var changeCount = host.ViewChanges.Count;

        await adaptor.OnProtocolMessageReceived(Notification(1, "stale"));
        await TestPhase.Await(adaptor.PostOnDeactivate(), "stale custom notification to drain");

        Assert.Equal(2, adaptor.ConfirmedVersion);
        Assert.Equal(["one", "two"], adaptor.ConfirmedView.Entries);
        Assert.Equal(changeCount, host.ViewChanges.Count);
        Assert.Empty(adaptor.UnconfirmedSuffix);
    }

    [Fact]
    public async Task ReadAsync_OnFreshAdaptor_RecoversStoredStateWithoutAliasing()
    {
        var host = new DeterministicCustomStorageHost();
        host.SetStoredState(["one", "two"], 2);
        var (adaptor, _, _) = CreateAdaptor(host);

        await Activate(adaptor, "fresh custom adaptor");

        Assert.Equal(1, host.ReadCount);
        Assert.Equal(2, adaptor.ConfirmedVersion);
        Assert.Equal(["one", "two"], adaptor.ConfirmedView.Entries);
        adaptor.ConfirmedView.Entries.Add("cache-only");
        Assert.Equal(["one", "two"], host.StoredState.Entries);
    }

    [Fact]
    public async Task ClearLogAsync_ResetsStorageAndFreshAdaptorRecoversVersionZero()
    {
        var (adaptor, host, _) = CreateAdaptor();
        Assert.True(await TestPhase.Await(
            adaptor.TryAppendRange(Entries("one", "two")),
            "custom storage setup range to commit"));
        await TestPhase.Await(adaptor.PostOnDeactivate(), "custom storage setup writer to become idle");

        await TestPhase.Await(
            adaptor.ClearLogAsync(CancellationToken.None),
            "custom storage clear to complete");
        await TestPhase.Await(adaptor.PostOnDeactivate(), "custom storage clear worker to become idle");

        Assert.Equal(1, host.ClearCount);
        Assert.Equal(0, host.StoredVersion);
        Assert.Empty(host.StoredState.Entries);
        Assert.Equal(0, adaptor.ConfirmedVersion);
        Assert.Empty(adaptor.ConfirmedView.Entries);
        Assert.Empty(adaptor.TentativeView.Entries);

        var (fresh, _, _) = CreateAdaptor(host);
        await Activate(fresh, "fresh custom adaptor after clear");
        Assert.Equal(0, fresh.ConfirmedVersion);
        Assert.Empty(fresh.ConfirmedView.Entries);
    }

    private static async Task Activate(CustomStorageAdaptor adaptor, string phase)
    {
        await adaptor.PreOnActivate();
        await adaptor.PostOnActivate();
        await TestPhase.Await(adaptor.PostOnDeactivate(), $"{phase} activation read to finish");
    }

    private static (TestCustomStorageAdaptor Adaptor, DeterministicCustomStorageHost Host, RecordingProtocolServices Services)
        CreateAdaptor(DeterministicCustomStorageHost? host = null)
    {
        host ??= new DeterministicCustomStorageHost();
        var services = new RecordingProtocolServices();
        return (new TestCustomStorageAdaptor(host, new TestLogView(), services), host, services);
    }

    private static CustomUpdateNotification Notification(int version, params string[] values) =>
        new()
        {
            Version = version,
            Updates = Entries(values).ToList(),
        };

    private static TestLogEntry[] Entries(params string[] values) =>
        values.Select(value => new TestLogEntry(value)).ToArray();
}

internal sealed class TestCustomStorageAdaptor(
    DeterministicCustomStorageHost host,
    TestLogView initialState,
    RecordingProtocolServices services)
    : CustomStorageAdaptor(host, initialState, services, primaryCluster: null);
