using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;
using TestExtensions;
using Xunit;

namespace Orleans.TestingHost.Tests;

[TestCategory("BVT")]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("TestingHost")]
public class TestClusterFatalErrorHandlerTests
{
    [Theory]
    [InlineData(1, false, false)]
    [InlineData(2, false, false)]
    [InlineData(2, false, true)]
    [InlineData(1, true, false)]
    [InlineData(2, true, false)]
    [InlineData(2, true, true)]
    public async Task FatalNotificationsCancelDrainingAndScheduleOneHostStop(
        int serviceCount,
        bool hasStopFailure,
        bool nestExceptions)
    {
        var token = TestContext.Current.CancellationToken;
        var logger = new StopLogger();
        var probes = Enumerable.Range(0, serviceCount).Select(_ => new DrainProbe()).ToArray();
        var stopFailure = hasStopFailure ? new InvalidOperationException("Hosted service stop failed.") : null;
        probes[0].StopException = stopFailure;
        if (nestExceptions)
        {
            foreach (var probe in probes)
            {
                probe.StopException = new AggregateException(
                    new AggregateException(probe.StopException ?? new OperationCanceledException(new CancellationToken(canceled: true))));
            }
        }

        using var host = new HostBuilder()
            .ConfigureServices((_, services) =>
            {
                TestClusterFatalErrorHandler.Configure(services);
                services.AddSingleton<ILogger<TestClusterHostTerminator>>(logger);
                foreach (var probe in probes)
                {
                    services.AddSingleton<IHostedService>(probe);
                }
            })
            .Build();
        TestClusterFatalErrorHandler.Attach(host);
        await host.StartAsync(token);
        var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = lifetime.ApplicationStopped.Register(() => stopped.TrySetResult());

        try
        {
            var handler = host.Services.GetRequiredService<IFatalErrorHandler>();
            handler.OnFatalException(context: "Invalidated membership incarnation");
            handler.OnFatalException(context: "Repeated notification");

            foreach (var probe in probes)
            {
                var drainToken = await probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
                Assert.True(drainToken.IsCancellationRequested);
            }

            await stopped.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
            var entry = await logger.Entry.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
            Assert.All(probes, probe =>
            {
                Assert.Equal(1, probe.StopCalls);
                Assert.False(probe.Release.Task.IsCompleted);
            });
            if (hasStopFailure)
            {
                Assert.Equal(LogLevel.Error, entry.Level);
                Assert.Equal("Failed to stop a test silo after a fatal error.", entry.Message);
                if (serviceCount > 1)
                {
                    var aggregate = Assert.IsType<AggregateException>(entry.Exception);
                    Assert.Contains(stopFailure, aggregate.Flatten().InnerExceptions);
                }
                else
                {
                    Assert.Same(stopFailure, entry.Exception);
                }
            }
            else
            {
                Assert.Equal(LogLevel.Debug, entry.Level);
                Assert.Equal("Canceled graceful draining after a fatal test silo error.", entry.Message);
                Assert.Null(entry.Exception);
            }
        }
        finally
        {
            foreach (var probe in probes)
            {
                probe.StopException = null;
                probe.Release.TrySetResult();
            }

            await host.StopAsync(token);
        }
    }

    private sealed class DrainProbe : IHostedService
    {
        public TaskCompletionSource<CancellationToken> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int StopCalls { get; private set; }
        public Exception? StopException { get; set; }

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken)
        {
            StopCalls++;
            Started.TrySetResult(cancellationToken);
            return StopException is { } exception ? Task.FromException(exception) : Release.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed class StopLogger : ILogger<TestClusterHostTerminator>
    {
        public TaskCompletionSource<(LogLevel Level, string Message, Exception? Exception)> Entry { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entry.TrySetResult((logLevel, formatter(state, exception), exception));
    }
}
