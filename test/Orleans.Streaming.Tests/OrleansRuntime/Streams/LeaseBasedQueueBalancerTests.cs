using System.Collections.Concurrent;
using System.Net;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Orleans.Configuration;
using Orleans.LeaseProviders;
using Orleans.Runtime;
using Orleans.Streams;
using Xunit;

namespace UnitTests.OrleansRuntime.Streams;

public class LeaseBasedQueueBalancerTests
{
    [Fact, TestCategory("BVT"), TestCategory("Streaming")]
    public async Task Shutdown_DrainsQueuedMembershipUpdatesBeforeDisposingTimers()
    {
        var siloAddress = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 13000), 1);
        var membershipUpdates = Channel.CreateUnbounded<ClusterMembershipSnapshot>();
        var membership = Substitute.For<IClusterMembershipService>();
        membership.MembershipUpdates.Returns(membershipUpdates.Reader.ReadAllAsync());
        var localSiloDetails = Substitute.For<ILocalSiloDetails>();
        localSiloDetails.SiloAddress.Returns(siloAddress);
        using var services = new ServiceCollection()
            .AddSingleton(membership)
            .AddSingleton(localSiloDetails)
            .BuildServiceProvider();

        var queueId = QueueId.GetQueueId("queue", 0, 0);
        var queueMapper = Substitute.For<IStreamQueueMapper>();
        queueMapper.GetAllQueues().Returns([queueId]);
        var acquireStarted = new TaskCompletionSource<LeaseRequest[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var acquireCompleted = new TaskCompletionSource<AcquireLeaseResult[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var leaseProvider = Substitute.For<ILeaseProvider>();
        leaseProvider.Acquire(Arg.Any<string>(), Arg.Any<LeaseRequest[]>()).Returns(call =>
        {
            acquireStarted.TrySetResult(call.Arg<LeaseRequest[]>());
            return acquireCompleted.Task;
        });
        leaseProvider.Release(Arg.Any<string>(), Arg.Any<AcquiredLease[]>()).Returns(Task.CompletedTask);
        var options = new LeaseBasedQueueBalancerOptions
        {
            LeaseAcquisitionPeriod = TimeSpan.FromDays(1),
            LeaseLength = TimeSpan.FromDays(2),
            LeaseRenewPeriod = TimeSpan.FromDays(1),
        };
        var recordingLoggerFactory = new RecordingLoggerFactory();
        var balancer = new TestLeaseBasedQueueBalancer(
            "provider",
            options,
            leaseProvider,
            services,
            recordingLoggerFactory,
            new FakeTimeProvider());

        await balancer.Initialize(queueMapper);
        balancer.NotifyClusterMembershipChange([siloAddress]);
        var requests = await acquireStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var shutdownTask = balancer.Shutdown();
        var acquiredLease = new AcquiredLease(requests[0].ResourceKey, options.LeaseLength, "token", DateTime.UtcNow);

        try
        {
            await Assert.ThrowsAsync<TimeoutException>(
                () => shutdownTask.WaitAsync(TimeSpan.FromMilliseconds(100)));
        }
        finally
        {
            acquireCompleted.TrySetResult([new AcquireLeaseResult(acquiredLease, ResponseCode.OK, null)]);
            await shutdownTask;
        }

        await leaseProvider.Received(1).Release(
            options.LeaseCategory,
            Arg.Is<AcquiredLease[]>(leases => leases.Length == 1 && leases[0] == acquiredLease));

        // The original bug allowed a queued membership update to touch the PeriodicTimer instances after
        // they were disposed, which logged "Error updating lease responsibilities." with an inner
        // ObjectDisposedException. Assert that shutdown completed cleanly, with no such error logged.
        Assert.DoesNotContain(recordingLoggerFactory.Entries, entry => entry.Exception is ObjectDisposedException);
        Assert.DoesNotContain(recordingLoggerFactory.Entries, entry => entry.Level >= LogLevel.Error);
    }

    private sealed class TestLeaseBasedQueueBalancer(
        string name,
        LeaseBasedQueueBalancerOptions options,
        ILeaseProvider leaseProvider,
        IServiceProvider services,
        ILoggerFactory loggerFactory,
        TimeProvider timeProvider)
        : LeaseBasedQueueBalancer(name, options, leaseProvider, services, loggerFactory, timeProvider)
    {
        public void NotifyClusterMembershipChange(HashSet<SiloAddress> activeSilos) => OnClusterMembershipChange(activeSilos);
    }

    /// <summary>
    /// An <see cref="ILoggerFactory"/> that records every log entry so tests can assert on logged
    /// errors, such as the <see cref="ObjectDisposedException"/> that was previously logged when a
    /// queued membership update accessed a disposed <see cref="PeriodicTimer"/> during shutdown.
    /// </summary>
    private sealed class RecordingLoggerFactory : ILoggerFactory
    {
        private readonly ConcurrentQueue<LogEntry> _entries = new();

        public IReadOnlyCollection<LogEntry> Entries => _entries.ToArray();

        public ILogger CreateLogger(string categoryName) => new RecordingLogger(categoryName, _entries);

        public void AddProvider(ILoggerProvider provider) { }

        public void Dispose() { }

        public readonly record struct LogEntry(LogLevel Level, string Category, Exception Exception, string Message);

        private sealed class RecordingLogger(string category, ConcurrentQueue<LogEntry> entries) : ILogger
        {
            public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
                => entries.Enqueue(new LogEntry(logLevel, category, exception, formatter(state, exception)));
        }
    }
}
