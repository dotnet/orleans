using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans;
using Orleans.CodeGeneration;
using Orleans.Configuration;
using Orleans.Connections;
using Orleans.Messaging;
using Orleans.Runtime;
using Orleans.Runtime.Messaging;
using Orleans.Serialization;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.Configuration;
using Orleans.Serialization.Invocation;
using Orleans.Serialization.Serializers;
using Orleans.Serialization.Session;
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
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public async Task QueuedOwnedGrainCancellation_RetiresArgumentsWithoutInvocation(bool decoded, bool cleanupThrows, bool oneWay)
    {
        using var probe = new OwnedRuntimeProbe();
        var builder = new InProcessTestClusterBuilder(1);
        builder.ConfigureHost(hostBuilder =>
        {
            TestDefaultConfiguration.ConfigureHostConfiguration(hostBuilder.Configuration);
            hostBuilder.Services.AddSingleton<ILoggerProvider>(new OwnedRuntimeLoggerProvider(probe));
        });
        await using var cluster = builder.Build();
        await cluster.DeployAsync(TestContext.Current.CancellationToken);
        var grain = cluster.Client.GetGrain<IOwnedRuntimeCalls>(probe.Id);
        using var heldArgument = new OwnedRuntimeArgument { OperationId = probe.Id, Bytes = [11, 23, 47] };
        var heldCall = grain.Run(heldArgument, false, TestContext.Current.CancellationToken);
        Message? queued = null;
        OwnedRuntimeArgument? queuedArgument = null;
        try
        {
            await probe.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            Assert.True(cluster.TryGetGrainContext(grain.GetGrainId(), out var context));
            var activation = Assert.IsType<ActivationData>(context);
            using var callerArgument = new OwnedRuntimeArgument { OperationId = probe.Id, Bytes = [3, 5, 7] };
            using var copiedRequest = CreateOwnedRequest(cluster.Silos[0].ServiceProvider, callerArgument);
            var request = copiedRequest;
            if (decoded)
            {
                var serializer = cluster.Silos[0].ServiceProvider.GetRequiredService<Serializer<IInvokable>>();
                request = Assert.IsAssignableFrom<IInvokable>(serializer.Deserialize(serializer.SerializeToArray(copiedRequest)));
            }
            using var ownedRequest = request;
            queuedArgument = Assert.IsType<OwnedRuntimeArgument>(request.GetArgument(0));
            Assert.NotSame(callerArgument, queuedArgument);
            Assert.NotSame(callerArgument.Bytes, queuedArgument.Bytes);
            if (cleanupThrows) queuedArgument.DisposeFailure = probe.CleanupFailure;
            queued = new Message
            {
                Direction = oneWay ? Message.Directions.OneWay : Message.Directions.Request,
                Id = new CorrelationId(721),
                SendingGrain = grain.GetGrainId(),
                SendingSilo = cluster.Silos[0].SiloAddress,
                TargetGrain = grain.GetGrainId(),
                BodyObject = request,
            };
            activation.ReceiveMessage(queued);
            Assert.Equal(1, activation.WaitingCount);
            await ((IGrainCallCancellationExtension)activation).CancelRequestAsync(
                queued.SendingGrain, queued.Id, TestContext.Current.CancellationToken);
            Assert.Equal(0, activation.WaitingCount);
            Assert.Equal(1, queuedArgument.DisposeCount);
            Assert.Empty(queuedArgument.Bytes);
            Assert.Null(queued.BodyObject);
            Assert.False(Assert.IsAssignableFrom<IInvokableArgumentOwner>(request).TryRetainArgumentResources());
            Assert.Equal(1, probe.InvocationCount);
            Assert.Equal(0, callerArgument.DisposeCount);
            Assert.Equal(new byte[] { 3, 5, 7 }, callerArgument.Bytes);
            if (cleanupThrows) Assert.Same(probe.CleanupFailure, Assert.Single(probe.CleanupLogs));
            else Assert.Empty(probe.CleanupLogs);
        }
        finally
        {
            probe.Release.TrySetResult();
            await heldCall;
            await cluster.StopAllSilosAsync(TestContext.Current.CancellationToken);
            queued?.Dispose();
        }

        Assert.NotNull(queuedArgument);
        Assert.Equal(1, queuedArgument.DisposeCount);
        Assert.Equal(1, probe.InvocationCount);
    }

    [Fact]
    public async Task QueuedGrainCancellation_ReleasesUnreadBodyWithoutDecoding()
    {
        using var probe = new OwnedRuntimeProbe();
        var builder = new InProcessTestClusterBuilder(1);
        builder.ConfigureHost(hostBuilder =>
        {
            TestDefaultConfiguration.ConfigureHostConfiguration(hostBuilder.Configuration);
            hostBuilder.Services.Configure<LoggerFilterOptions>(options =>
            {
                options.Rules.Clear();
                options.MinLevel = LogLevel.Warning;
            });
        });
        await using var cluster = builder.Build();
        await cluster.DeployAsync(TestContext.Current.CancellationToken);
        var grain = cluster.Client.GetGrain<IOwnedRuntimeCalls>(probe.Id);
        using var argument = new OwnedRuntimeArgument { OperationId = probe.Id, Bytes = [11, 23, 47] };
        var heldCall = grain.Run(argument, false, TestContext.Current.CancellationToken);
        Message? queued = null;
        try
        {
            await probe.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            Assert.True(cluster.TryGetGrainContext(grain.GetGrainId(), out var context));
            var activation = Assert.IsType<ActivationData>(context);
            using var writer = new ArcBufferWriter();
            writer.Write(new byte[] { 255, 255, 255 });
            using var original = writer.PeekSlice(writer.Length);
            var services = cluster.Silos[0].ServiceProvider;
            var decodes = 0;
            using var shared = new MessageHandlerShared(
                services.GetRequiredService<MessagingTrace>(), services.GetRequiredService<ConnectionTrace>(),
                () => { decodes++; return new MessageSerializer(services.GetRequiredService<SerializerSessionPool>(), new SiloMessagingOptions()); },
                services.GetRequiredService<MessageFactory>(), services.GetRequiredService<IMessageCenter>(),
                services.GetRequiredService<MessagingInstruments>());
            var read = new MessageReadRequest(shared)
            { Body = original.Slice(0) };
            queued = new Message
            {
                Direction = Message.Directions.Request,
                Id = new CorrelationId(722),
                SendingGrain = grain.GetGrainId(),
                SendingSilo = cluster.Silos[0].SiloAddress,
                TargetGrain = grain.GetGrainId(),
            };
            queued.SetMessageReadRequest(read);
            activation.ReceiveMessage(queued);
            Assert.Equal(1, activation.WaitingCount);

            await ((IGrainCallCancellationExtension)activation).CancelRequestAsync(
                queued.SendingGrain, queued.Id, TestContext.Current.CancellationToken);

            Assert.Equal(0, activation.WaitingCount);
            Assert.Null(queued.BodyObject);
            Assert.Equal(0, read.Body.Length);
            Assert.Equal(0, decodes);
            Assert.Equal(new byte[] { 255, 255, 255 }, original.ToArray());
            Assert.Equal(1, probe.InvocationCount);
        }
        finally
        {
            probe.Release.TrySetResult();
            await heldCall;
            await cluster.StopAllSilosAsync(TestContext.Current.CancellationToken);
            queued?.Dispose();
        }
    }

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
            Interlocked.Increment(ref probe.InvocationCount);
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
        [NonSerialized] public Exception? DisposeFailure;
        [Id(0)] public Guid OperationId { get; set; }
        [Id(1)] public byte[] Bytes { get; set; } = [];
        public int DisposeCount => _disposeCount;
        public void Dispose()
        {
            Interlocked.Increment(ref _disposeCount);
            Bytes = [];
            var probe = OwnedRuntimeProbe.Active[OperationId];
            if (DisposeFailure is { } failure) throw failure;
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
        internal int InvocationCount;
        internal OwnedRuntimeProbe() => Active[Id] = this;
        public void Dispose() => Active.TryRemove(Id, out _);
    }

    private static IInvokable CreateOwnedRequest(IServiceProvider services, OwnedRuntimeArgument argument)
    {
        var capture = new CapturingRuntime();
        var shared = new GrainReferenceShared(GrainType.Create("owned-runtime"), GrainInterfaceType.Create("owned-runtime"),
            0, capture, InvokeMethodOptions.None, services.GetRequiredService<CodecProvider>(),
            services.GetRequiredService<CopyContextPool>(), services);
        var proxyType = Assert.Single(services.GetRequiredService<IOptions<TypeManifestOptions>>().Value.InterfaceProxies,
            type => typeof(IOwnedRuntimeCalls).IsAssignableFrom(type));
        var proxy = Assert.IsAssignableFrom<IOwnedRuntimeCalls>(Activator.CreateInstance(proxyType, shared, IdSpan.Create("queued")));
        Assert.True(proxy.Run(argument, false, CancellationToken.None).IsCompletedSuccessfully);
        return Assert.Single(capture.Requests);
    }

    private sealed class CapturingRuntime : IGrainReferenceRuntime
    {
        internal List<IInvokable> Requests { get; } = [];
        public ValueTask<T?> InvokeMethodAsync<T>(GrainReference reference, IInvokable request, InvokeMethodOptions options)
        { Requests.Add(request); return default; }
        public ValueTask InvokeMethodAsync(GrainReference reference, IInvokable request, InvokeMethodOptions options)
        { Requests.Add(request); return default; }
        public void InvokeMethod(GrainReference reference, IInvokable request, InvokeMethodOptions options) => Requests.Add(request);
        public object Cast(IAddressable grain, Type interfaceType) => grain;
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
