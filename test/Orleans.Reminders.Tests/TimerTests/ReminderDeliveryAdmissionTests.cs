using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;
using Orleans.Reminders.Diagnostics;
using Orleans.Runtime;
using Orleans.Runtime.ReminderService;
using Orleans.Serialization.Invocation;
using Orleans.Testing.Reminders;
using Orleans.TestingHost;
using Orleans.TestingHost.Diagnostics;
using TestExtensions;
using Xunit;

namespace UnitTests.TimerTests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Reminders")]
[TestCategory("BVT"), TestCategory("Reminders")]
public class ReminderDeliveryAdmissionTests
{
    private const string AdmittedReminder = "admitted";
    private const string LateReminder = "late";
    private static readonly TimeSpan Period = TimeSpan.FromMinutes(1);

    [Theory]
    [InlineData(DeliveryOutcome.Success)]
    [InlineData(DeliveryOutcome.Failure)]
    [InlineData(DeliveryOutcome.Canceled)]
    public async Task Stop_WaitsForAdmittedDeliveryAndRejectsLateTicks(DeliveryOutcome outcome)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.CancelAfter(TestConstants.InitTimeout);
        await using var test = new DeliveryTest();
        await test.InitializeAsync(cancellation.Token);
        test.Late.Completion.SetResult();
        await test.RegisterAsync(AdmittedReminder, TimeSpan.FromSeconds(1), cancellation.Token);
        await test.RegisterAsync(LateReminder, TimeSpan.FromSeconds(2), cancellation.Token);
        await test.Clock.AdvanceAsync(TimeSpan.FromSeconds(1), cancellation.Token);
        await test.Admitted.Entered.Task.WaitAsync(cancellation.Token);

        // Repeated startup preserves the run which owns the admitted delivery.
        await test.QueueAsync(test.Service.Start).WaitAsync(cancellation.Token);
        var stop = await test.BeginStopAsync(CancellationToken.None, cancellation.Token);
        var repeatedStop = await test.BeginStopAsync(CancellationToken.None, cancellation.Token);
        Assert.False(stop.IsCompleted);
        Assert.False(repeatedStop.IsCompleted);

        test.Diagnostics.Clear();
        var lateTickSkipped = test.WaitForEventAsync<ReminderEvents.LocalReminderTickWaitArmed>(
            LateReminder, cancellation.Token);
        await test.Clock.AdvanceAsync(TimeSpan.FromSeconds(1), cancellation.Token);
        await lateTickSkipped;

        Assert.Equal(1, test.Admitted.CallCount);
        Assert.Equal(0, test.Late.CallCount);
        Assert.Empty(test.GetEvents<ReminderEvents.TickFiring>());
        Assert.False(stop.IsCompleted);
        Assert.False(repeatedStop.IsCompleted);
        Assert.Equal(1, test.Clock.DiagnosticObserver.GetActiveReminderCount(test.GrainId, AdmittedReminder));
        Assert.Equal(1, test.Clock.DiagnosticObserver.GetActiveReminderCount(test.GrainId, LateReminder));

        var failure = new InvalidOperationException("Controlled reminder delivery failure.");
        switch (outcome)
        {
            case DeliveryOutcome.Success:
                test.Admitted.Completion.SetResult();
                break;
            case DeliveryOutcome.Failure:
                test.Admitted.Completion.SetException(failure);
                break;
            case DeliveryOutcome.Canceled:
                test.Admitted.Completion.SetCanceled(new CancellationToken(canceled: true));
                break;
        }

        await Task.WhenAll(stop, repeatedStop).WaitAsync(cancellation.Token);
        Assert.Equal(0, test.Clock.DiagnosticObserver.GetActiveReminderCount(test.GrainId, AdmittedReminder));
        Assert.Equal(0, test.Clock.DiagnosticObserver.GetActiveReminderCount(test.GrainId, LateReminder));
        Assert.Equal(0, test.Late.CallCount);
        if (outcome is DeliveryOutcome.Success)
        {
            Assert.Equal(1, test.Clock.DiagnosticObserver.GetTickCount(test.GrainId, AdmittedReminder));
            Assert.Empty(test.GetEvents<ReminderEvents.TickFailed>());
        }
        else
        {
            var failed = await test.WaitForEventAsync<ReminderEvents.TickFailed>(AdmittedReminder, cancellation.Token);
            Assert.Equal(0, test.Clock.DiagnosticObserver.GetTickCount(test.GrainId, AdmittedReminder));
            if (outcome is DeliveryOutcome.Failure)
            {
                Assert.Same(failure, failed.Exception);
            }
            else
            {
                Assert.IsAssignableFrom<OperationCanceledException>(failed.Exception);
            }
        }
    }

    [Fact]
    public async Task Stop_CanceledWaitContinuesDraining()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.CancelAfter(TestConstants.InitTimeout);
        await using var test = new DeliveryTest();
        await test.InitializeAsync(cancellation.Token);
        await test.RegisterAsync(AdmittedReminder, TimeSpan.FromSeconds(1), cancellation.Token);
        await test.Clock.AdvanceAsync(TimeSpan.FromSeconds(1), cancellation.Token);
        await test.Admitted.Entered.Task.WaitAsync(cancellation.Token);

        using var stopCancellation = new CancellationTokenSource();
        var stop = await test.BeginStopAsync(stopCancellation.Token, cancellation.Token);
        Assert.False(stop.IsCompleted);
        stopCancellation.Cancel();
        var canceled = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stop);
        Assert.Equal(stopCancellation.Token, canceled.CancellationToken);
        Assert.False(test.Admitted.Completion.Task.IsCompleted);
        Assert.Equal(1, test.Clock.DiagnosticObserver.GetActiveReminderCount(test.GrainId, AdmittedReminder));

        var stopped = test.WaitForEventAsync<ReminderEvents.LocalReminderStopped>(AdmittedReminder, cancellation.Token);
        test.Admitted.Completion.SetResult();
        Assert.Equal(ReminderEvents.LocalReminderStopReason.ServiceStopped, (await stopped).Reason);
        await test.Clock.DiagnosticObserver.WaitForReminderQuiescenceAsync(test.GrainId, AdmittedReminder, cancellation.Token);
        Assert.Equal(1, test.Clock.DiagnosticObserver.GetTickCount(test.GrainId, AdmittedReminder));
        Assert.Equal(0, test.Clock.DiagnosticObserver.GetActiveReminderCount(test.GrainId, AdmittedReminder));
        await test.QueueAsync(test.Service.Stop).WaitAsync(cancellation.Token);
    }

    [Fact]
    public async Task Start_AfterCompletedStop_ResumesReminderDelivery()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.CancelAfter(TestConstants.InitTimeout);
        await using var test = new DeliveryTest();
        await test.InitializeAsync(cancellation.Token);
        test.Admitted.Completion.SetResult();
        await test.RegisterAsync(AdmittedReminder, TimeSpan.FromSeconds(1), cancellation.Token);
        await test.Clock.AdvanceAsync(TimeSpan.FromSeconds(1), cancellation.Token);
        await test.Clock.DiagnosticObserver.WaitForTickCountAsync(test.GrainId, 1, cancellation.Token, AdmittedReminder);
        await test.QueueAsync(test.Service.Stop).WaitAsync(cancellation.Token);
        Assert.Equal(0, test.Clock.DiagnosticObserver.GetActiveReminderCount(test.GrainId, AdmittedReminder));

        await test.QueueAsync(test.Service.Start).WaitAsync(cancellation.Token);
        await test.Service.TestOnlyRefresh().WaitAsync(cancellation.Token);
        await test.Clock.DiagnosticObserver.WaitForLocalReminderScheduleAsync(test.GrainId, AdmittedReminder, cancellation.Token);
        await test.Clock.AdvanceAsync(Period, cancellation.Token);
        await test.Clock.DiagnosticObserver.WaitForTickCountAsync(test.GrainId, 2, cancellation.Token, AdmittedReminder);

        Assert.Equal(2, test.Admitted.CallCount);
        Assert.Equal(2, test.Clock.DiagnosticObserver.GetLocalStartCount(test.GrainId, AdmittedReminder));
        Assert.Equal(1, test.Clock.DiagnosticObserver.GetActiveReminderCount(test.GrainId, AdmittedReminder));
        await test.QueueAsync(test.Service.Stop).WaitAsync(cancellation.Token);
        Assert.Equal(2, test.Clock.DiagnosticObserver.GetLocalStopCount(test.GrainId, AdmittedReminder));
        Assert.Equal(0, test.Clock.DiagnosticObserver.GetActiveReminderCount(test.GrainId, AdmittedReminder));
    }

    [Fact]
    public async Task Start_DuringCanceledStop_WaitsForCleanupBeforeReopeningAdmission()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.CancelAfter(TestConstants.InitTimeout);
        await using var test = new DeliveryTest();
        await test.InitializeAsync(cancellation.Token);
        await test.RegisterAsync(AdmittedReminder, TimeSpan.FromSeconds(1), cancellation.Token);
        await test.Clock.AdvanceAsync(TimeSpan.FromSeconds(1), cancellation.Token);
        await test.Admitted.Entered.Task.WaitAsync(cancellation.Token);

        using var stopCancellation = new CancellationTokenSource();
        var stop = await test.BeginStopAsync(stopCancellation.Token, cancellation.Token);
        stopCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stop);

        using var startCancellation = new CancellationTokenSource();
        var canceledStart = await test.BeginStartAsync(startCancellation.Token, cancellation.Token);
        Assert.False(canceledStart.IsCompleted);
        startCancellation.Cancel();
        var canceled = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledStart);
        Assert.Equal(startCancellation.Token, canceled.CancellationToken);
        Assert.False(test.Admitted.Completion.Task.IsCompleted);

        var restart = await test.BeginStartAsync(CancellationToken.None, cancellation.Token);
        var repeatedStop = await test.BeginStopAsync(CancellationToken.None, cancellation.Token);
        await test.QueueAsync(() => test.Service.RegisterOrUpdateReminder(
            test.GrainId, LateReminder, TimeSpan.FromSeconds(1), Period)).WaitAsync(cancellation.Token);
        Assert.False(restart.IsCompleted);
        Assert.False(repeatedStop.IsCompleted);
        Assert.Equal(0, test.Clock.DiagnosticObserver.GetLocalStartCount(test.GrainId, LateReminder));
        Assert.Equal(0, test.Late.CallCount);

        test.Admitted.Completion.SetResult();
        await Task.WhenAll(restart, repeatedStop).WaitAsync(cancellation.Token);
        await test.Service.TestOnlyRefresh().WaitAsync(cancellation.Token);
        await Task.WhenAll(
            test.Clock.DiagnosticObserver.WaitForLocalReminderScheduleAsync(test.GrainId, AdmittedReminder, cancellation.Token),
            test.Clock.DiagnosticObserver.WaitForLocalReminderScheduleAsync(test.GrainId, LateReminder, cancellation.Token));
        Assert.Equal(1, test.Clock.DiagnosticObserver.GetLocalStopCount(test.GrainId, AdmittedReminder));
        Assert.Equal(2, test.Clock.DiagnosticObserver.GetLocalStartCount(test.GrainId, AdmittedReminder));
        Assert.Equal(1, test.Clock.DiagnosticObserver.GetLocalStartCount(test.GrainId, LateReminder));

        await test.Clock.AdvanceAsync(TimeSpan.FromSeconds(1), cancellation.Token);
        await test.Late.Entered.Task.WaitAsync(cancellation.Token);
        await test.Clock.DiagnosticObserver.WaitForTickCountAsync(test.GrainId, 2, cancellation.Token, AdmittedReminder);
        var nextStop = await test.BeginStopAsync(CancellationToken.None, cancellation.Token);
        Assert.False(nextStop.IsCompleted);
        Assert.Equal(1, test.Late.CallCount);
        test.Late.Completion.SetResult();
        await nextStop.WaitAsync(cancellation.Token);
        Assert.Equal(1, test.Clock.DiagnosticObserver.GetTickCount(test.GrainId, LateReminder));
        Assert.Equal(0, test.Clock.DiagnosticObserver.GetActiveReminderCount(test.GrainId, AdmittedReminder));
        Assert.Equal(0, test.Clock.DiagnosticObserver.GetActiveReminderCount(test.GrainId, LateReminder));
    }

    public enum DeliveryOutcome
    {
        Success,
        Failure,
        Canceled,
    }

    private sealed class ControlledDelivery
    {
        private int _callCount;
        public int CallCount => Volatile.Read(ref _callCount);
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Invoke()
        {
            Interlocked.Increment(ref _callCount);
            Entered.TrySetResult();
            return Completion.Task;
        }
    }

    private sealed class DeliveryTest : IAsyncDisposable
    {
        private readonly InProcessTestCluster _cluster;

        public DeliveryTest()
        {
            var builder = new InProcessTestClusterBuilder(1);
            Clock = builder.AddReminderTestClock();
            builder.ConfigureSilo((_, siloBuilder) =>
            {
                siloBuilder.UseInMemoryReminderService();
                siloBuilder.AddOutgoingGrainCallFilter(async context =>
                {
                    if (context.TargetId == GrainId && context.InterfaceMethod.DeclaringType == typeof(IRemindable))
                    {
                        var reminderName = (string)context.Request.GetArgument(0)!;
                        await (reminderName == AdmittedReminder ? Admitted : Late).Invoke();
                        context.Response = Response.Completed;
                        return;
                    }

                    await context.Invoke();
                });
            });
            _cluster = builder.Build();
        }

        public GrainId GrainId { get; } = GrainId.Create("test", Guid.NewGuid().ToString("N"));
        public ControlledDelivery Admitted { get; } = new();
        public ControlledDelivery Late { get; } = new();
        public ReminderTestClock Clock { get; }
        public DiagnosticEventCollector Diagnostics { get; } = new(ReminderEvents.ListenerName);
        public LocalReminderService Service { get; private set; } = null!;

        public async Task InitializeAsync(CancellationToken cancellationToken)
        {
            await _cluster.DeployAsync(cancellationToken);
            await ReminderTopologyStabilizer.WaitForStartupTopologyAsync(
                _cluster, Clock.DiagnosticObserver, _cluster.Silos, cancellationToken);
            Service = Assert.Single(_cluster.Silos).ServiceProvider.GetRequiredService<LocalReminderService>();
        }

        public async Task RegisterAsync(string reminderName, TimeSpan dueTime, CancellationToken cancellationToken)
        {
            await QueueAsync(() => Service.RegisterOrUpdateReminder(GrainId, reminderName, dueTime, Period))
                .WaitAsync(cancellationToken);
            await Clock.DiagnosticObserver.WaitForLocalReminderScheduleAsync(GrainId, reminderName, cancellationToken);
        }

        public Task<Task> BeginStopAsync(CancellationToken stopCancellation, CancellationToken cancellationToken)
            => Queue(() => Service.Stop(stopCancellation)).WaitAsync(cancellationToken);

        public Task<Task> BeginStartAsync(CancellationToken startCancellation, CancellationToken cancellationToken)
            => Queue(() => Service.Start(startCancellation)).WaitAsync(cancellationToken);

        public Task QueueAsync(Func<Task> action) => Queue(action).Unwrap();

        private Task<Task> Queue(Func<Task> action)
        {
            var task = new Task<Task>(action);
            Service.Scheduler.QueueTask(task);
            return task;
        }

        public IEnumerable<T> GetEvents<T>() where T : ReminderEvents.ReminderEvent
            => Diagnostics.GetEvents(typeof(T).Name)
                .Select(diagnostic => diagnostic.Payload)
                .OfType<T>()
                .Where(reminder => reminder.GrainId == GrainId);

        public async Task<T> WaitForEventAsync<T>(string reminderName, CancellationToken cancellationToken)
            where T : ReminderEvents.ReminderEvent
        {
            var result = await Diagnostics.WaitForEventAsync(
                typeof(T).Name,
                diagnostic => diagnostic.Payload is T reminder
                    && reminder.GrainId == GrainId && reminder.ReminderName == reminderName,
                TestConstants.InitTimeout,
                cancellationToken);
            return Assert.IsType<T>(result.Payload);
        }

        public async ValueTask DisposeAsync()
        {
            Admitted.Completion.TrySetResult();
            Late.Completion.TrySetResult();
            try
            {
                using var cleanup = new CancellationTokenSource(TestConstants.InitTimeout);
                await _cluster.StopAllSilosAsync(cleanup.Token);
                await _cluster.DisposeAsync().AsTask().WaitAsync(cleanup.Token);
            }
            finally
            {
                Clock.Dispose();
                Diagnostics.Dispose();
            }
        }
    }
}
