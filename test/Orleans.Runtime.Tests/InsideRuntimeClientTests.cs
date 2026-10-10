using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Runtime;
using Orleans.TestingHost;
using TestExtensions;
using Xunit;

namespace Tester;

[TestSuite("BVT")]
[TestProvider("None")]
[TestCategory("BVT")]
public class InsideRuntimeClientTests
{
    [Theory]
    [InlineData("success", false)]
    [InlineData("throw", false)]
    [InlineData("cancel", false)]
    [InlineData("success", true)]
    [InlineData("throw", true)]
    [InlineData("cancel", true)]
    public async Task OwnedRpcInvocation_RetainsDecodedArgumentThroughActualExit(string outcome, bool cleanupThrows)
    {
        using var probe = new OwnedRuntimeProbe { CleanupThrows = cleanupThrows };
        var builder = new InProcessTestClusterBuilder(1);
        builder.ConfigureHost(hostBuilder =>
        {
            TestDefaultConfiguration.ConfigureHostConfiguration(hostBuilder.Configuration);
            hostBuilder.Services.AddSingleton<ILoggerProvider>(new OwnedRuntimeLoggerProvider(probe));
        });
        await using var cluster = builder.Build();
        await cluster.DeployAsync(TestContext.Current.CancellationToken);
        using var original = new OwnedRuntimeArgument { OperationId = probe.Id, Bytes = [11, 23, 47] };
        using var cancellation = new CancellationTokenSource();
        var call = cluster.Client.GetGrain<IOwnedRuntimeCalls>(probe.Id).Run(original, outcome == "throw", cancellation.Token);
        try
        {
            var received = await probe.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            Assert.NotSame(original, received);
            Assert.NotSame(original.Bytes, received.Bytes);
            Assert.Equal(original.Bytes, received.Bytes);
            Assert.Equal(0, received.DisposeCount);
            if (outcome == "cancel")
            {
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
                Assert.Equal(0, received.DisposeCount);
                Assert.Equal(original.Bytes, received.Bytes);
            }
        }
        finally
        {
            probe.Release.TrySetResult();
        }

        if (outcome == "success") await call;
        if (outcome == "throw")
        {
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => call);
            Assert.Equal("owned invocation failed", failure.Message);
        }

        await probe.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        await cluster.StopAllSilosAsync(TestContext.Current.CancellationToken);
        var invoked = await probe.Entered.Task;
        Assert.Equal(1, invoked.DisposeCount);
        Assert.Empty(invoked.Bytes);
        Assert.False(probe.DisposedBeforeExit);
        Assert.Equal(new byte[] { 11, 23, 47 }, probe.Observed);
        Assert.Equal(0, original.DisposeCount);
        Assert.Equal(new byte[] { 11, 23, 47 }, original.Bytes);
        if (cleanupThrows) Assert.Same(probe.CleanupFailure, Assert.Single(probe.CleanupLogs));
        else Assert.Empty(probe.CleanupLogs);
    }

    [Fact]
    public async Task LateRejectionAfterSiloDisposalDoesNotResolveServices()
    {
        var builder = new InProcessTestClusterBuilder(1);
        builder.ConfigureHost(hostBuilder => TestDefaultConfiguration.ConfigureHostConfiguration(hostBuilder.Configuration));
        await using var cluster = builder.Build();
        await cluster.DeployAsync(TestContext.Current.CancellationToken);

        var silo = cluster.Silos[0];
        var runtimeClient = silo.ServiceProvider.GetRequiredService<InsideRuntimeClient>();
        var rejection = new Message
        {
            Direction = Message.Directions.Response,
            Result = Message.ResponseTypes.Rejection,
            TargetSilo = silo.SiloAddress,
            TargetGrain = GrainId.Create("caller", Guid.NewGuid().ToString()),
            SendingGrain = GrainId.Create("target", Guid.NewGuid().ToString()),
            BodyObject = new RejectionResponse
            {
                RejectionType = Message.RejectionTypes.Unrecoverable,
                RejectionInfo = "The outbound queue is stopped",
            },
        };

        await silo.StopSiloAsync(stopGracefully: false);
        await silo.DisposeAsync();

        Assert.Null(Record.Exception(() => runtimeClient.ReceiveResponse(rejection)));
    }

    public interface IOwnedRuntimeCalls : IGrainWithGuidKey
    {
        Task Run([DisposeOnCompletion] OwnedRuntimeArgument argument, bool fail, CancellationToken cancellationToken);
    }

    public sealed class OwnedRuntimeCalls : Grain, IOwnedRuntimeCalls
    {
        public async Task Run(OwnedRuntimeArgument argument, bool fail, CancellationToken cancellationToken)
        {
            var probe = OwnedRuntimeProbe.Active[argument.OperationId];
            probe.Entered.TrySetResult(argument);
            try
            {
                await probe.Release.Task;
                probe.Observed = argument.Bytes.ToArray();
                if (fail) throw new InvalidOperationException("owned invocation failed");
            }
            finally
            {
                probe.Exited = true;
            }
        }
    }

    [GenerateSerializer]
    public sealed class OwnedRuntimeArgument : IDisposable
    {
        [NonSerialized] private int _disposeCount;
        [Id(0)] public Guid OperationId { get; set; }
        [Id(1)] public byte[] Bytes { get; set; } = [];
        public int DisposeCount => _disposeCount;
        public void Dispose()
        {
            Interlocked.Increment(ref _disposeCount);
            Bytes = [];
            var probe = OwnedRuntimeProbe.Active[OperationId];
            if (probe.Entered.Task.IsCompletedSuccessfully && ReferenceEquals(this, probe.Entered.Task.Result))
            {
                probe.DisposedBeforeExit = !probe.Exited;
                probe.Disposed.TrySetResult();
                if (probe.CleanupThrows) throw probe.CleanupFailure;
            }
        }
    }

    internal sealed class OwnedRuntimeProbe : IDisposable
    {
        internal static ConcurrentDictionary<Guid, OwnedRuntimeProbe> Active { get; } = new();
        internal Guid Id { get; } = Guid.NewGuid();
        internal TaskCompletionSource<OwnedRuntimeArgument> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool CleanupThrows { get; init; }
        internal Exception CleanupFailure { get; } = new InvalidOperationException("owned invocation cleanup failed");
        internal ConcurrentQueue<Exception> CleanupLogs { get; } = new();
        internal byte[] Observed { get; set; } = [];
        internal bool Exited { get; set; }
        internal bool DisposedBeforeExit { get; set; }
        internal OwnedRuntimeProbe() => Active[Id] = this;
        public void Dispose() => Active.TryRemove(Id, out _);
    }

    internal sealed class OwnedRuntimeLoggerProvider(OwnedRuntimeProbe probe) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new ProbeLogger(probe);
        public void Dispose() { }
        private sealed class ProbeLogger(OwnedRuntimeProbe probe) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (ReferenceEquals(exception, probe.CleanupFailure)) probe.CleanupLogs.Enqueue(exception!);
            }
        }
    }
}
