using System.Net;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Orleans.Runtime;
using Orleans.Streams;
using Xunit;

namespace UnitTests.OrleansRuntime.Streams;

[TestSuite("BVT"), TestProvider("None"), TestArea("Streaming")]
public class QueueBalancerBaseTests
{
    [Theory, TestCategory("BVT"), TestCategory("Streaming")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Shutdown_InvokesCallbacksOnCallerAndDrainsMembership(bool callbackThrows)
    {
        var enumerationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var enumerationStopping = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseEnumeration = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var membership = Substitute.For<IClusterMembershipService>();
        membership.MembershipUpdates.Returns(ReadMembership(TestContext.Current.CancellationToken));
        var localSilo = Substitute.For<ILocalSiloDetails>();
        localSilo.SiloAddress.Returns(SiloAddress.New(IPAddress.Loopback, 13000, 1));
        using var services = new ServiceCollection()
            .AddSingleton(membership)
            .AddSingleton(localSilo)
            .BuildServiceProvider();
        var logger = new RecordingLogger();
        var balancer = new TestQueueBalancer(services, logger);
        var callbackThreads = new List<int>();
        var callbackFailure = new InvalidOperationException("Cancellation callback failed.");
        using var firstCallback = balancer.ShutdownToken.Register(() => callbackThreads.Add(Environment.CurrentManagedThreadId));
        using var secondCallback = balancer.ShutdownToken.Register(() =>
        {
            callbackThreads.Add(Environment.CurrentManagedThreadId);
            if (callbackThrows)
            {
                throw callbackFailure;
            }
        });

        await balancer.Initialize(Substitute.For<IStreamQueueMapper>());
        try
        {
            await enumerationStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            var callingThread = Environment.CurrentManagedThreadId;
            var shutdown = balancer.Shutdown();
            Assert.Equal(new[] { callingThread, callingThread }, callbackThreads);
            await enumerationStopping.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.False(shutdown.IsCompleted);
            var overlappingShutdown = balancer.Shutdown();
            Assert.False(overlappingShutdown.IsCompleted);

            releaseEnumeration.SetResult();
            await Task.WhenAll(shutdown, overlappingShutdown).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            await balancer.Shutdown();
            Assert.Equal(2, callbackThreads.Count);
            if (callbackThrows)
            {
                var entry = Assert.Single(logger.Entries);
                Assert.Equal(LogLevel.Error, entry.Level);
                Assert.Equal("Error signaling shutdown token.", entry.Message);
                Assert.Same(callbackFailure, Assert.Single(Assert.IsType<AggregateException>(entry.Exception).InnerExceptions));
            }
            else
            {
                Assert.Empty(logger.Entries);
            }
        }
        finally
        {
            releaseEnumeration.TrySetResult();
            await balancer.Shutdown().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        }

        async IAsyncEnumerable<ClusterMembershipSnapshot> ReadMembership([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            try
            {
                enumerationStarted.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            finally
            {
                enumerationStopping.SetResult();
                await releaseEnumeration.Task;
            }

            yield break;
        }
    }

    private sealed class TestQueueBalancer(IServiceProvider services, ILogger logger) : QueueBalancerBase(services, logger)
    {
        public CancellationToken ShutdownToken => Cancellation;
        public override IEnumerable<QueueId> GetMyQueues() => [];
        protected override void OnClusterMembershipChange(HashSet<SiloAddress> activeSilos) { }
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception), exception));
    }
}
