using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenTelemetry.Metrics;
using Orleans;
using Orleans.Concurrency;
using Orleans.Dashboard;
using Orleans.Dashboard.Core;
using Orleans.Dashboard.Implementation;
using Orleans.Dashboard.Model;
using Orleans.Hosting;
using Orleans.Runtime;
using Xunit;

namespace UnitTests;

[TestCategory("BVT")]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Dashboard")]
public class DashboardHostTests
{
    private const string DashboardActivationWarning =
        "Unable to activate dashboard grain during startup. The grain will be activated on first use.";
    private const string SiloActivationWarning =
        "Unable to activate silo grain service during startup. The service will be activated on first use.";

    private static readonly FieldInfo MeterProviderField = typeof(DashboardHost)
        .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
        .Single(field => field.FieldType == typeof(MeterProvider));

    [Fact]
    public void SiloWithoutDashboard_DoesNotEnableMessageSizeHistograms()
    {
        using var host = new HostBuilder()
            .ConfigureLogging(logging => logging.ClearProviders())
            .UseOrleans(silo => silo.UseLocalhostClustering())
            .Build();
        host.Services.GetRequiredService<Silo>();
        var meter = host.Services.GetRequiredService<OrleansInstruments>().Meter;

        Assert.False(meter.CreateHistogram<int>("orleans-messaging-received-messages-size", "bytes").Enabled);
        Assert.False(meter.CreateHistogram<int>("orleans-messaging-sent-messages-size", "bytes").Enabled);
    }

    [Fact]
    public async Task Participate_CollectsClientConnectionsBeforeHostedServiceStartup()
    {
        using var fixture = new HostFixture();
        var clients = fixture.Instruments.Meter.CreateUpDownCounter<int>("orleans-gateway-connected-clients");
        await using var host = fixture.CreateHost();
        await StartTelemetryAsync(host);

        clients.Add(2);
        await host.StartAsync(TestContext.Current.CancellationToken);
        Assert.True(GetMeterProvider(host).ForceFlush());
        AssertCounter(Assert.Single(fixture.CounterReports), clients.Name, "2", "2");
    }

    [Fact]
    public async Task StartAsync_ExportsRuntimeMeterCounters()
    {
        using var fixture = new HostFixture();
        var counter = fixture.Instruments.Meter.CreateCounter<long>("dashboard.test.messages");
        await using var host = fixture.CreateHost();
        await StartHostAsync(host);

        counter.Add(7);
        Assert.True(GetMeterProvider(host).ForceFlush());

        var report = Assert.Single(fixture.CounterReports);
        var exported = Assert.Single(report, value => value.Name == counter.Name);
        Assert.Equal("7", exported.Value);
        Assert.Equal("7", exported.Delta);
    }

    [Fact]
    public async Task StartAsync_EnablesOnlyTheSilosMeterUntilDisposal()
    {
        using var fixture = new HostFixture();
        using var otherServices = new ServiceCollection().AddMetrics().BuildServiceProvider();
        var otherInstruments = new OrleansInstruments(otherServices.GetRequiredService<IMeterFactory>());
        var ownHistogram = fixture.Instruments.Meter.CreateHistogram<int>("dashboard.test.bytes");
        var otherHistogram = otherInstruments.Meter.CreateHistogram<int>(ownHistogram.Name);
        Assert.Equal(fixture.Instruments.Meter.Name, otherInstruments.Meter.Name);
        Assert.False(ownHistogram.Enabled);
        Assert.False(otherHistogram.Enabled);
        ownHistogram.Record(999);

        await using var host = fixture.CreateHost();
        await StartHostAsync(host);
        Assert.True(ownHistogram.Enabled);
        Assert.False(otherHistogram.Enabled);
        ownHistogram.Record(123, new KeyValuePair<string, object?>("direction", "request"));
        ownHistogram.Record(456, new KeyValuePair<string, object?>("direction", "response"));
        otherHistogram.Record(999);
        Assert.True(GetMeterProvider(host).ForceFlush());
        AssertCounter(Assert.Single(fixture.CounterReports), ownHistogram.Name, "579", "579");

        await host.DisposeAsync();
        Assert.False(ownHistogram.Enabled);
        Assert.False(otherHistogram.Enabled);
    }

    [Fact]
    public async Task StartAsync_PreservesTaggedGaugeTotals()
    {
        using var fixture = new HostFixture();
        var count = 2L;
        fixture.Instruments.Meter.CreateObservableGauge("dashboard.test.tagged-gauge", () => new[]
        {
            new Measurement<long>(count, new KeyValuePair<string, object?>("grain_type", "one")),
            new Measurement<long>(3, new KeyValuePair<string, object?>("grain_type", "two")),
        });
        await using var host = fixture.CreateHost();
        await StartHostAsync(host);

        Assert.True(GetMeterProvider(host).ForceFlush());
        AssertCounter(Assert.Single(fixture.CounterReports), "dashboard.test.tagged-gauge", "5", "5");
        count = 7;
        Assert.True(GetMeterProvider(host).ForceFlush());
        Assert.Equal(2, fixture.CounterReports.Count);
        AssertCounter(fixture.CounterReports.Last(), "dashboard.test.tagged-gauge", "10", "5");
    }

    private static void AssertCounter(StatCounter[] report, string name, string value, string delta)
    {
        var counter = Assert.Single(report, counter => counter.Name == name);
        Assert.Equal(value, counter.Value);
        Assert.Equal(delta, counter.Delta);
    }

    [Fact]
    public async Task StartAsync_ValidDependencies_ActivatesDashboardGrainAndSiloGrainServiceOnce()
    {
        using var fixture = new HostFixture();
        var host = fixture.CreateHost();

        try
        {
            await StartHostAsync(host);

            AssertDashboardActivation(fixture);
            AssertSiloActivation(fixture);
            Assert.NotNull(GetMeterProvider(host));
            Assert.Empty(fixture.Logger.Entries);
        }
        finally
        {
            await host.DisposeAsync();
        }
    }

    [Fact]
    public async Task StartAsync_DashboardGrainActivationThrows_LogsWarningAndContinues()
    {
        var expectedException = new InvalidOperationException("dashboard initialization failed");
        using var fixture = new HostFixture { DashboardInitializeException = expectedException };
        var host = fixture.CreateHost();

        try
        {
            await StartHostAsync(host);

            AssertDashboardActivation(fixture);
            AssertSiloActivation(fixture);
            AssertWarning(
                Assert.Single(fixture.Logger.Entries),
                DashboardActivationWarning,
                expectedException);
        }
        finally
        {
            await host.DisposeAsync();
        }
    }

    [Fact]
    public async Task StartAsync_SiloGrainServiceActivationThrows_LogsWarningAndContinues()
    {
        var expectedException = new InvalidOperationException("version update failed");
        using var fixture = new HostFixture { SiloSetVersionException = expectedException };
        var host = fixture.CreateHost();

        try
        {
            await StartHostAsync(host);

            AssertDashboardActivation(fixture);
            AssertSiloActivation(fixture);
            AssertWarning(
                Assert.Single(fixture.Logger.Entries),
                SiloActivationWarning,
                expectedException);
        }
        finally
        {
            await host.DisposeAsync();
        }
    }

    [Fact]
    public async Task StartAsync_BothActivationsThrow_LogsBothWarningsWithoutFailingHostStartup()
    {
        var dashboardException = new InvalidOperationException("dashboard initialization failed");
        var siloException = new InvalidOperationException("version update failed");
        using var fixture = new HostFixture
        {
            DashboardInitializeException = dashboardException,
            SiloSetVersionException = siloException,
        };
        var host = fixture.CreateHost();

        try
        {
            await StartHostAsync(host);

            AssertDashboardActivation(fixture);
            AssertSiloActivation(fixture);
            Assert.Equal(2, fixture.Logger.Entries.Count);
            AssertWarning(
                Assert.Single(fixture.Logger.Entries, entry => entry.Message == DashboardActivationWarning),
                DashboardActivationWarning,
                dashboardException);
            AssertWarning(
                Assert.Single(fixture.Logger.Entries, entry => entry.Message == SiloActivationWarning),
                SiloActivationWarning,
                siloException);
        }
        finally
        {
            await host.DisposeAsync();
        }
    }

    [Fact]
    public async Task StopAsync_AfterStart_CompletesAndDoesNotReactivateServices()
    {
        using var fixture = new HostFixture();
        var host = fixture.CreateHost();
        await StartHostAsync(host);
        var originalProvider = GetMeterProvider(host);
        var trackingProvider = new TrackingMeterProvider();
        SetMeterProvider(host, trackingProvider);

        try
        {
            await host.StopAsync(TestContext.Current.CancellationToken);

            AssertDashboardActivation(fixture);
            AssertSiloActivation(fixture);
            Assert.Equal(0, trackingProvider.DisposeCalls);
        }
        finally
        {
            originalProvider.Dispose();
            host.Dispose();
        }

        Assert.Equal(1, trackingProvider.DisposeCalls);
    }

    [Fact]
    public async Task StopAsync_CalledTwice_RemainsSafe()
    {
        using var fixture = new HostFixture();
        var host = fixture.CreateHost();
        var trackingProvider = new TrackingMeterProvider();
        SetMeterProvider(host, trackingProvider);

        var firstStop = host.StopAsync(TestContext.Current.CancellationToken);
        var secondStop = host.StopAsync(TestContext.Current.CancellationToken);

        await firstStop;
        await secondStop;
        Assert.True(firstStop.IsCompletedSuccessfully);
        Assert.True(secondStop.IsCompletedSuccessfully);
        Assert.Equal(0, fixture.DashboardGrainFactoryCalls);
        Assert.Equal(0, fixture.SiloGrainClientCalls);
        Assert.Equal(0, trackingProvider.DisposeCalls);

        host.Dispose();
        Assert.Equal(1, trackingProvider.DisposeCalls);
    }

    [Fact]
    public void Dispose_TelemetryDisposalThrows_DoesNotThrow()
    {
        using var fixture = new HostFixture();
        var host = fixture.CreateHost();
        var expectedException = new InvalidOperationException("synchronous telemetry disposal failed");
        var throwingProvider = new ThrowingMeterProvider(expectedException);
        SetMeterProvider(host, throwingProvider);

        var escapedException = Record.Exception(host.Dispose);

        Assert.Null(escapedException);
        Assert.Equal(1, throwingProvider.DisposeCalls);
        Assert.Same(expectedException, throwingProvider.ThrownException);
    }

    [Fact]
    public async Task DisposeAsync_TelemetryDisposalThrows_DoesNotThrowOrSynchronouslyDispose()
    {
        using var fixture = new HostFixture();
        var host = fixture.CreateHost();
        var expectedException = new InvalidOperationException("asynchronous telemetry disposal failed");
        var throwingProvider = new ThrowingAsyncMeterProvider(expectedException);
        SetMeterProvider(host, throwingProvider);

        var escapedException = await Record.ExceptionAsync(async () => await host.DisposeAsync());

        Assert.Null(escapedException);
        Assert.Equal(1, throwingProvider.AsyncDisposeCalls);
        Assert.Equal(0, throwingProvider.SyncDisposeCalls);
        Assert.Same(expectedException, throwingProvider.ThrownException);
    }

    [Fact]
    public async Task DisposeAsync_SynchronousTelemetryProvider_DisposesExactlyOnce()
    {
        using var fixture = new HostFixture();
        var host = fixture.CreateHost();
        var trackingProvider = new TrackingMeterProvider();
        SetMeterProvider(host, trackingProvider);

        await host.DisposeAsync();

        Assert.Equal(1, trackingProvider.DisposeCalls);
    }

    private static void AssertDashboardActivation(HostFixture fixture)
    {
        Assert.Equal(1, fixture.DashboardGrainFactoryCalls);
        Assert.Equal(1, fixture.DashboardInitializeCalls);
        var method = Assert.IsAssignableFrom<MethodInfo>(fixture.DashboardGrainFactoryMethod);
        Assert.Equal(nameof(IGrainFactory.GetGrain), method.Name);
        Assert.Equal(typeof(IDashboardGrain), Assert.Single(method.GetGenericArguments()));
        Assert.Equal(2, fixture.DashboardGrainFactoryArguments!.Length);
        Assert.Equal(0L, Assert.IsType<long>(fixture.DashboardGrainFactoryArguments[0]));
        Assert.Null(fixture.DashboardGrainFactoryArguments[1]);
    }

    private static void AssertSiloActivation(HostFixture fixture)
    {
        Assert.Equal([fixture.SiloAddress], fixture.SiloGrainDestinations);
        var versions = Assert.Single(fixture.SetVersionCalls);
        Assert.Equal(ExpectedOrleansVersion(), versions.OrleansVersion);
        Assert.Equal(ExpectedHostVersion(), versions.HostVersion);
    }

    private static void AssertWarning(CapturedLog entry, string message, Exception exception)
    {
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal(message, entry.Message);
        Assert.Same(exception, entry.Exception);
    }

    private static string ExpectedOrleansVersion()
    {
        var assembly = typeof(SiloAddress).GetTypeInfo().Assembly;
        return $"{assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion} ({assembly.GetName().Version})";
    }

    private static string ExpectedHostVersion()
    {
        try
        {
            var assembly = Assembly.GetEntryAssembly();
            if (assembly is not null)
            {
                return assembly.GetName().Version!.ToString();
            }
        }
        catch
        {
        }

        return "1.0.0.0";
    }

    private static MeterProvider GetMeterProvider(DashboardHost host) =>
        Assert.IsAssignableFrom<MeterProvider>(MeterProviderField.GetValue(host));

    private static async Task StartHostAsync(DashboardHost host)
    {
        await StartTelemetryAsync(host);
        await host.StartAsync(TestContext.Current.CancellationToken);
    }

    private static async Task StartTelemetryAsync(DashboardHost host)
    {
        ILifecycleObserver? observer = null;
        var lifecycle = CreateProxy<ISiloLifecycle>((method, arguments) =>
        {
            Assert.Equal(nameof(ISiloLifecycle.Subscribe), method.Name);
            Assert.Equal(ServiceLifecycleStage.BecomeActive, Assert.IsType<int>(arguments![1]));
            observer = Assert.IsAssignableFrom<ILifecycleObserver>(arguments[2]);
            return NullScope.Instance;
        });
        host.Participate(lifecycle);
        Assert.NotNull(observer);
        await observer.OnStart(TestContext.Current.CancellationToken);
    }

    private static void SetMeterProvider(DashboardHost host, MeterProvider meterProvider) =>
        MeterProviderField.SetValue(host, meterProvider);

    private static T CreateProxy<T>(Func<MethodInfo, object?[]?, object?> handler)
        where T : class
    {
        var result = DispatchProxy.Create<T, MethodDispatchProxy>();
        ((MethodDispatchProxy)(object)result).Handler = handler;
        return result;
    }

    private sealed class HostFixture : IDisposable
    {
        private readonly ServiceProvider _services = new ServiceCollection().AddMetrics().BuildServiceProvider();
        private readonly IDashboardGrain _dashboardGrain;
        private readonly IGrainFactory _grainFactory;
        private readonly ISiloGrainService _siloGrainService;
        private readonly ISiloGrainClient _siloGrainClient;
        private readonly DashboardTelemetryExporter _telemetryExporter;

        public HostFixture()
        {
            Instruments = new OrleansInstruments(_services.GetRequiredService<IMeterFactory>());
            LocalSiloDetails = new TestLocalSiloDetails();
            SiloAddress = LocalSiloDetails.SiloAddress;
            _dashboardGrain = CreateProxy<IDashboardGrain>((method, _) =>
            {
                if (method.Name != nameof(IDashboardGrain.InitializeAsync))
                {
                    throw new NotSupportedException(method.Name);
                }

                DashboardInitializeCalls++;
                return DashboardInitializeException is null
                    ? Task.CompletedTask
                    : Task.FromException(DashboardInitializeException);
            });
            _grainFactory = CreateProxy<IGrainFactory>((method, arguments) =>
            {
                DashboardGrainFactoryCalls++;
                DashboardGrainFactoryMethod = method;
                DashboardGrainFactoryArguments = arguments;
                return _dashboardGrain;
            });
            _siloGrainService = CreateProxy<ISiloGrainService>((method, arguments) =>
            {
                if (method.Name != nameof(ISiloGrainService.SetVersion))
                {
                    throw new NotSupportedException(method.Name);
                }

                SetVersionCalls.Add((
                    Assert.IsType<string>(arguments![0]),
                    Assert.IsType<string>(arguments[1])));
                return SiloSetVersionException is null
                    ? Task.CompletedTask
                    : Task.FromException(SiloSetVersionException);
            });
            _siloGrainClient = new RecordingSiloGrainClient(
                _siloGrainService,
                destination => SiloGrainDestinations.Add(destination));

            var telemetryService = CreateProxy<ISiloGrainService>((method, arguments) =>
            {
                if (method.Name != nameof(ISiloGrainService.ReportCounters))
                {
                    throw new NotSupportedException(method.Name);
                }

                CounterReports.Enqueue(Assert.IsType<Immutable<StatCounter[]>>(arguments![0]).Value);
                return Task.CompletedTask;
            });
            var telemetryClient = new RecordingSiloGrainClient(telemetryService);
            _telemetryExporter = new DashboardTelemetryExporter(
                LocalSiloDetails,
                telemetryClient,
                NullLogger<DashboardTelemetryExporter>.Instance);
        }

        public CapturingLogger<DashboardHost> Logger { get; } = new();

        public OrleansInstruments Instruments { get; }

        public TestLocalSiloDetails LocalSiloDetails { get; }

        public SiloAddress SiloAddress { get; }

        public Exception? DashboardInitializeException { get; init; }

        public Exception? SiloSetVersionException { get; init; }

        public int DashboardGrainFactoryCalls { get; private set; }

        public MethodInfo? DashboardGrainFactoryMethod { get; private set; }

        public object?[]? DashboardGrainFactoryArguments { get; private set; }

        public int DashboardInitializeCalls { get; private set; }

        public int SiloGrainClientCalls => SiloGrainDestinations.Count;

        public List<SiloAddress> SiloGrainDestinations { get; } = [];

        public List<(string OrleansVersion, string HostVersion)> SetVersionCalls { get; } = [];

        public ConcurrentQueue<StatCounter[]> CounterReports { get; } = new();

        public DashboardHost CreateHost() =>
            new(Logger, LocalSiloDetails, Instruments, _grainFactory, _telemetryExporter, _siloGrainClient)
            {
                ExportIntervalMilliseconds = Timeout.Infinite,
            };

        public void Dispose() => _services.Dispose();
    }

    private sealed class TestLocalSiloDetails : ILocalSiloDetails
    {
        public string Name => "dashboard-silo";

        public string ClusterId => "dashboard-cluster";

        public string DnsHostName => "dashboard.example";

        public SiloAddress SiloAddress { get; } = SiloAddress.New(IPAddress.Loopback, 11_111, 42);

        public SiloAddress GatewayAddress { get; } = SiloAddress.New(IPAddress.Loopback, 30_000, 42);
    }

    private sealed class RecordingSiloGrainClient(
        ISiloGrainService service,
        Action<SiloAddress>? onGrainService = null) : ISiloGrainClient
    {
        public ISiloGrainService GrainService(SiloAddress destination)
        {
            onGrainService?.Invoke(destination);
            return service;
        }
    }

    private sealed class TrackingMeterProvider : MeterProvider
    {
        public int DisposeCalls { get; private set; }

        protected override void Dispose(bool disposing)
        {
            DisposeCalls++;
            base.Dispose(disposing);
        }
    }

    private sealed class ThrowingMeterProvider(Exception exception) : MeterProvider
    {
        public int DisposeCalls { get; private set; }

        public Exception? ThrownException { get; private set; }

        protected override void Dispose(bool disposing)
        {
            if (!disposing)
            {
                base.Dispose(disposing);
                return;
            }

            DisposeCalls++;
            ThrownException = exception;
            throw exception;
        }
    }

    private sealed class ThrowingAsyncMeterProvider(Exception exception) : MeterProvider, IAsyncDisposable
    {
        public int AsyncDisposeCalls { get; private set; }

        public int SyncDisposeCalls { get; private set; }

        public Exception? ThrownException { get; private set; }

        public ValueTask DisposeAsync()
        {
            AsyncDisposeCalls++;
            ThrownException = exception;
            throw exception;
        }

        protected override void Dispose(bool disposing)
        {
            SyncDisposeCalls++;
            base.Dispose(disposing);
        }
    }

    private sealed record CapturedLog(LogLevel Level, string Message, Exception? Exception);

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        private readonly object _lock = new();
        private readonly List<CapturedLog> _entries = [];

        public IReadOnlyList<CapturedLog> Entries
        {
            get
            {
                lock (_lock)
                {
                    return [.. _entries];
                }
            }
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull =>
            NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_lock)
            {
                _entries.Add(new(logLevel, formatter(state, exception), exception));
            }
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static NullScope Instance { get; } = new();

        public void Dispose()
        {
        }
    }

    private class MethodDispatchProxy : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?> Handler { get; set; } =
            static (method, _) => throw new NotSupportedException(method.Name);

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            Handler(targetMethod!, args);
    }
}
