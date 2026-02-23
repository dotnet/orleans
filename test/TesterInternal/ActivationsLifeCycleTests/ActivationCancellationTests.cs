#nullable enable
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans.Configuration;
using Orleans.TestingHost;
using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;
using Xunit.Abstractions;

namespace UnitTests.ActivationCancellationTests;

/// <summary>
/// Tests for activation cancellation logging behavior in Orleans.
/// 
/// These tests verify that:
/// 1. When activation is cancelled gracefully (OperationCanceledException or ObjectDisposedException
///    thrown while cancellation token is cancelled), the exception is wrapped in ActivationCancelledException
///    and logged at WARNING level (not ERROR level).
/// 2. When non-cancellation exceptions occur, they are still logged at ERROR level as expected.
/// 
/// The key code being tested is in ActivationData.cs:
/// - catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested) → ActivationCancelledException → Warning log
/// - catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested &amp;&amp; !timeout) → ActivationCancelledException → Warning log
/// - catch (Exception) → Error log
/// 
/// Plan / Pseudocode (inserted as comment for traceability):
/// 1. Add two reusable assertion helpers:
///    - AssertLogEventExists(logs, eventId) -> asserts that any log in 'logs' has EventId == eventId.
///    - AssertLogEventNotExists(logs, eventId) -> asserts that no log in 'logs' has EventId == eventId.
/// 2. Keep existing behavior: ensure OperationCanceledException scenario finds info logs with the cancelled-activate EventId.
/// 3. Additionally assert that across error logs there is NOT an event with EventId == (int)ErrorCode.Catalog_ErrorCallingActivate.
/// 4. Helpers include clear diagnostic messages listing captured EventIds for easier debugging.
/// 5. Place helpers as private methods on the test class so they can be reused by other tests.
///
/// The code below implements these helpers and uses them in the OperationCanceledException test.
/// </summary>
public class ActivationCancellationLoggingTests : OrleansTestingBase, IClassFixture<ActivationCancellationLoggingTests.Fixture>
{
    private readonly Fixture _fixture;
    private readonly ITestOutputHelper _output;

    public class Fixture : BaseTestClusterFixture
    {
        // Static so it can be accessed from the configurator
        public static InMemoryLoggerProvider SharedLoggerProvider { get; } = new();

        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            builder.Options.InitialSilosCount = 1;
            builder.AddSiloBuilderConfigurator<SiloHostConfigurator>();
        }

        private class SiloHostConfigurator : ISiloConfigurator, IHostConfigurator
        {
            public void Configure(ISiloBuilder hostBuilder)
            {
                hostBuilder.Configure<GrainCollectionOptions>(options =>
                {
                    options.CollectionAge = TimeSpan.FromSeconds(10);
                    options.CollectionQuantum = TimeSpan.FromSeconds(1);
                    // Short activation timeout so we can test cancellation scenarios
                    options.ActivationTimeout = TimeSpan.FromSeconds(2);
                    options.DeactivationTimeout = TimeSpan.FromSeconds(2);
                });

                hostBuilder.Configure<SiloMessagingOptions>(options =>
                {
                    options.MaxRequestProcessingTime = TimeSpan.FromSeconds(5);
                });
            }

            public void Configure(IHostBuilder hostBuilder)
            {
                hostBuilder.ConfigureServices(services =>
                {
                    services.AddSingleton<ILoggerProvider>(SharedLoggerProvider);
                });
            }
        }
    }

    public ActivationCancellationLoggingTests(Fixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
        Fixture.SharedLoggerProvider.Clear();
    }

    private (IReadOnlyList<LogEntry> ErrorLogs, IReadOnlyList<LogEntry> WarningLogs, IReadOnlyList<LogEntry> InfoLogs) GetActivationLogs()
    {
        var logs = Fixture.SharedLoggerProvider.GetLogs();
        
        // Log all captured entries for debugging
        _output.WriteLine($"Total logs captured: {logs.Count}");
        foreach (var log in logs.Where(l => l.Level >= LogLevel.Warning))
        {
            _output.WriteLine($"[{log.Level}] [{log.Category}] {log.Message}");
        }

        // Look for activation-related logs 
        var activationLogs = logs.Where(l => 
            l.Category.Contains("Orleans.Grain") || 
            l.Category.Contains("ActivationData") ||
            l.Message.Contains("Activation", StringComparison.OrdinalIgnoreCase) ||
            l.Message.Contains("activating", StringComparison.OrdinalIgnoreCase)).ToList();

        var errorLogs = activationLogs.Where(l => l.Level == LogLevel.Error).ToList();
        var warningLogs = activationLogs.Where(l => l.Level == LogLevel.Warning).ToList();
        var infoLogs = activationLogs.Where(l => l.Level == LogLevel.Information).ToList();

        return (errorLogs, warningLogs, infoLogs);
    }

    // Reusable helper: assert that at least one log in 'logs' has the specified event id.
    private static void AssertLogEventExists(IEnumerable<LogEntry> logs, int eventId)
    {
        var ids = logs.Select(l => l.EventId.Id).ToList();
        Assert.True(ids.Any(id => id == eventId),
            $"Expected an information log with EventId {eventId} but it was not found. Captured EventIds: {string.Join(',', ids)}");
    }

    // Reusable helper: assert that no log in 'logs' has the specified event id.
    private static void AssertLogEventNotExists(IEnumerable<LogEntry> logs, int eventId)
    {
        var ids = logs.Select(l => l.EventId.Id).ToList();
        Assert.False(ids.Any(id => id == eventId),
            $"Did not expect a log with EventId {eventId}, but it was found. Captured EventIds: {string.Join(',', ids)}");
    }

    #region Cancellation Scenarios - Should NOT log ERROR

    /// <summary>
    /// When OperationCanceledException is thrown because the cancellation token was observed during activation,
    /// it should be logged at WARNING level (not ERROR) because this is intentional cancellation behavior.
    /// </summary>
    [Fact, TestCategory("Functional"), TestCategory("ActivationCancellation")]
    public async Task OperationCanceledException_WhenCancellationTokenObserved_LogsInfoNotError()
    {
        Fixture.SharedLoggerProvider.Clear();
        var grain = _fixture.GrainFactory.GetGrain<IActivationCancellation_ThrowsOperationCancelledGrain>(Guid.NewGuid());

        // Set a delay longer than the activation timeout (2 seconds) to trigger cancellation
        RequestContext.Set("delay_activation_ms", 5000);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await grain.IsActivated().WaitAsync(TimeSpan.FromSeconds(5));
        });

        RequestContext.Clear();

        await Task.Delay(100);

        var (errorLogs, warningLogs, infoLogs) = GetActivationLogs();

        Assert.Empty(errorLogs);
        Assert.Empty(warningLogs);
        Assert.NotEmpty(infoLogs);

        // New assertion: ensure there is an info log with EventId == (int)ErrorCode.Catalog_CancelledActivate
        AssertLogEventExists(infoLogs, (int)ErrorCode.Catalog_CancelledActivate);

        // Also ensure across error logs there is NOT an event with EventId == (int)ErrorCode.Catalog_ErrorCallingActivate
        AssertLogEventNotExists(errorLogs, (int)ErrorCode.Catalog_ErrorCallingActivate);
    }

    /// <summary>
    /// When ObjectDisposedException is thrown because services are disposed after cancellation,
    /// it should be logged at WARNING level (not ERROR) because this is expected behavior during cancellation.
    /// </summary>
    [Fact, TestCategory("Functional"), TestCategory("ActivationCancellation")]
    public async Task ObjectDisposedException_WhenCancellationRequested_LogsWarningNotError()
    {
        Fixture.SharedLoggerProvider.Clear();
        var grain = _fixture.GrainFactory.GetGrain<IActivationCancellation_ThrowsObjectDisposedGrain>(Guid.NewGuid());

        // Set a delay longer than the activation timeout to trigger cancellation
        RequestContext.Set("delay_activation_ms", 5000);

        await Assert.ThrowsAnyAsync<ObjectDisposedException>(async () =>
        {
            await grain.IsActivated();
        });

        RequestContext.Clear();

        await Task.Delay(100);

        var (errorLogs, warningLogs, infoLogs) = GetActivationLogs();

        Assert.Empty(errorLogs);
        Assert.NotEmpty(warningLogs);


        // New assertion: ensure there is an info log with EventId == (int)ErrorCode.Catalog_CancelledActivate
        AssertLogEventExists(warningLogs, (int)ErrorCode.Catalog_DisposedObjectAccess);

        // Also ensure across error logs there is NOT an event with EventId == (int)ErrorCode.Catalog_ErrorCallingActivate
        AssertLogEventNotExists(errorLogs, (int)ErrorCode.Catalog_ErrorCallingActivate);
    }

    /// <summary>
    /// When TaskCanceledException (which inherits from OperationCanceledException) is thrown during cancellation,
    /// it should be logged at INFO level (not ERROR).
    /// </summary>
    [Fact, TestCategory("Functional"), TestCategory("ActivationCancellation")]
    public async Task TaskCanceledException_WhenCancellationRequested_LogsInfoNotError()
    {
        Fixture.SharedLoggerProvider.Clear();
        var grain = _fixture.GrainFactory.GetGrain<IActivationCancellation_ThrowsTaskCancelledGrain>(Guid.NewGuid());

        RequestContext.Set("delay_activation_ms", 5000);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await grain.IsActivated().WaitAsync(TimeSpan.FromSeconds(5));
        });

        RequestContext.Clear();

        await Task.Delay(100);

        var (errorLogs, warningLogs, infoLogs) = GetActivationLogs();

        Assert.Empty(errorLogs);
        Assert.Empty(warningLogs);
        Assert.NotEmpty(infoLogs);

        // New assertion: ensure there is an info log with EventId == (int)ErrorCode.Catalog_CancelledActivate
        AssertLogEventExists(infoLogs, (int)ErrorCode.Catalog_CancelledActivate);

        // Also ensure across error logs there is NOT an event with EventId == (int)ErrorCode.Catalog_ErrorCallingActivate
        AssertLogEventNotExists(errorLogs, (int)ErrorCode.Catalog_ErrorCallingActivate);
    }

    #endregion

    #region Non-Cancellation Scenarios - SHOULD log ERROR

    /// <summary>
    /// When a generic exception (not related to cancellation) is thrown during activation,
    /// it should be logged at ERROR level because this is an unexpected failure.
    /// </summary>
    [Fact, TestCategory("BVT"), TestCategory("ActivationCancellation")]
    public async Task GenericException_DuringActivation_LogsError()
    {
        Fixture.SharedLoggerProvider.Clear();
        var grain = _fixture.GrainFactory.GetGrain<IActivationCancellation_ThrowsGenericExceptionGrain>(Guid.NewGuid());

        RequestContext.Set("throw_exception", true);

        await Assert.ThrowsAsync<InvalidOperationException>(() => grain.IsActivated());

        RequestContext.Clear();

        await Task.Delay(100);

        var (errorLogs, _, infoLogs) = GetActivationLogs();

        Assert.NotEmpty(errorLogs);

        // New assertion: ensure there is an info log with EventId == (int)ErrorCode.Catalog_CancelledActivate
        AssertLogEventExists(errorLogs, (int)ErrorCode.Catalog_ErrorCallingActivate);
    }

    /// <summary>
    /// When ObjectDisposedException is thrown but the cancellation token was NOT cancelled,
    /// it should be logged at ERROR level (the 'when' guard should NOT match).
    /// </summary>
    [Fact, TestCategory("BVT"), TestCategory("ActivationCancellation")]
    public async Task ObjectDisposedException_WhenNotCancelled_LogsError()
    {
        Fixture.SharedLoggerProvider.Clear();
        var grain = _fixture.GrainFactory.GetGrain<IActivationCancellation_ThrowsObjectDisposedUnconditionallyGrain>(Guid.NewGuid());

        RequestContext.Set("throw_object_disposed", true);

        await Assert.ThrowsAsync<ObjectDisposedException>(() => grain.IsActivated());

        RequestContext.Clear();

        await Task.Delay(100);

        var (errorLogs, _, infoLogs) = GetActivationLogs();

        Assert.NotEmpty(errorLogs);

        // New assertion: ensure there is an info log with EventId == (int)ErrorCode.Catalog_CancelledActivate
        AssertLogEventExists(errorLogs, (int)ErrorCode.Catalog_ErrorCallingActivate);
    }

    /// <summary>
    /// When OperationCanceledException is thrown but the cancellation token was NOT cancelled,
    /// it should be logged at ERROR level (the 'when' guard should NOT match).
    /// </summary>
    [Fact, TestCategory("BVT"), TestCategory("ActivationCancellation")]
    public async Task OperationCanceledException_WhenNotCancelled_LogsError()
    {
        Fixture.SharedLoggerProvider.Clear();
        var grain = _fixture.GrainFactory.GetGrain<IActivationCancellation_ThrowsOperationCancelledUnconditionallyGrain>(Guid.NewGuid());

        RequestContext.Set("throw_operation_cancelled", true);

        await Assert.ThrowsAsync<OperationCanceledException>(() => grain.IsActivated());

        RequestContext.Clear();

        await Task.Delay(100);

        var (errorLogs, _, infoLogs) = GetActivationLogs();

        Assert.NotEmpty(errorLogs);

        // New assertion: ensure there is an info log with EventId == (int)ErrorCode.Catalog_CancelledActivate
        AssertLogEventExists(errorLogs, (int)ErrorCode.Catalog_ErrorCallingActivate);
    }

    #endregion

    #region Baseline Tests - Normal Operation

    /// <summary>
    /// Baseline test: Successful activation should not log any errors or warnings.
    /// </summary>
    [Fact, TestCategory("BVT"), TestCategory("ActivationCancellation")]
    public async Task SuccessfulActivation_NoErrorOrWarningLogs()
    {
        Fixture.SharedLoggerProvider.Clear();
        var grain = _fixture.GrainFactory.GetGrain<IActivationCancellation_SuccessfulActivationGrain>(Guid.NewGuid());

        var isActivated = await grain.IsActivated();

        await Task.Delay(100);

        var (errorLogs, warningLogs, infoLogs) = GetActivationLogs();

        Assert.True(isActivated);
        Assert.Empty(errorLogs);
        Assert.Empty(warningLogs);

        // New assertion: ensure there is an info log with EventId == (int)ErrorCode.Catalog_CancelledActivate
        AssertLogEventNotExists(infoLogs, (int)ErrorCode.Catalog_CancelledActivate);
    }

    #endregion
}

public class InMemoryLoggerProvider : ILoggerProvider
{
    private readonly List<LogEntry> _logs = new();
    private readonly object _lock = new();

    public ILogger CreateLogger(string categoryName) => new InMemoryLogger(this, categoryName);

    public void Dispose() { }

    public void Clear()
    {
        lock (_lock)
        {
            _logs.Clear();
        }
    }

    public IReadOnlyList<LogEntry> GetLogs()
    {
        lock (_lock)
        {
            return _logs.ToList();
        }
    }

    internal void AddLog(LogEntry entry)
    {
        lock (_lock)
        {
            _logs.Add(entry);
        }
    }

    private class InMemoryLogger(InMemoryLoggerProvider provider, string categoryName) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            provider.AddLog(new LogEntry(categoryName, logLevel, eventId, formatter(state, exception), exception));
        }
    }
}

public record LogEntry(string Category, LogLevel Level, EventId EventId, string Message, Exception? Exception);
