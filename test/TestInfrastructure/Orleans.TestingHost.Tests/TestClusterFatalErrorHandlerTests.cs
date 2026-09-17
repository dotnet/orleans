using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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
    [Fact]
    public async Task FatalNotificationsCancelDrainingAndScheduleOneHostStop()
    {
        var token = TestContext.Current.CancellationToken;
        var probe = new DrainProbe();
        using var host = new HostBuilder()
            .ConfigureServices((_, services) =>
            {
                TestClusterFatalErrorHandler.Configure(services);
                services.AddSingleton<IHostedService>(probe);
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

            var drainToken = await probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
            Assert.True(drainToken.IsCancellationRequested);
            await stopped.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
            Assert.Equal(1, probe.StopCalls);
            Assert.False(probe.Release.Task.IsCompleted);
        }
        finally
        {
            probe.Release.TrySetResult();
            await host.StopAsync(token);
        }
    }

    private sealed class DrainProbe : IHostedService
    {
        public TaskCompletionSource<CancellationToken> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int StopCalls { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken)
        {
            StopCalls++;
            Started.TrySetResult(cancellationToken);
            return Release.Task.WaitAsync(cancellationToken);
        }
    }
}
