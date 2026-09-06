using Microsoft.Extensions.Logging;
using Orleans.Transactions.Abstractions;
using Orleans.Transactions.TestKit.Consistency;
using TestExtensions;
using Xunit;

namespace Orleans.Transactions.Tests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Transactions")]
[TestCategory("BVT"), TestCategory("Transactions")]
public class ConsistencyTestGrainInputValidationTests
{
    [Fact]
    public void Constructor_NullLoggerFactory_ThrowsWithLoggerFactoryParamNameBeforeCreateLogger()
    {
        var state = new RecordingTransactionalState();

        var exception = Assert.Throws<ArgumentNullException>(
            () => new ConsistencyTestGrain(state, loggerFactory: null!));

        Assert.Equal("loggerFactory", exception.ParamName);
        Assert.Equal(0, state.InteractionCount);
    }

    [Fact]
    public async Task Run_NullOptions_ThrowsWithOptionsParamNameBeforeRuntimeOrStateAccess()
    {
        var state = new RecordingTransactionalState();
        var loggerFactory = new RecordingLoggerFactory();
        var grain = new ConsistencyTestGrain(state, loggerFactory);

        var exception = await Assert.ThrowsAsync<ArgumentNullException>(
            () => grain.Run(
                options: null!,
                depth: 2,
                stack: "(4,7)",
                maxgrain: 13,
                stopAfter: DateTime.MaxValue));

        Assert.Equal("options", exception.ParamName);
        Assert.Equal(0, state.InteractionCount);
        Assert.Equal(0, loggerFactory.Logger.InteractionCount);
        Assert.Equal(1, loggerFactory.CreateLoggerCallCount);
        Assert.Equal("ConsistencyTestGrain.graincall", loggerFactory.LastCategoryName);
    }

    [Fact]
    public async Task Run_NullStack_ThrowsWithStackParamNameBeforeRuntimeOrStateAccess()
    {
        var state = new RecordingTransactionalState();
        var loggerFactory = new RecordingLoggerFactory();
        var grain = new ConsistencyTestGrain(state, loggerFactory);
        var options = new ConsistencyTestOptions
        {
            RandomSeed = 17,
            NumGrains = 23,
            MaxDepth = 4,
            AvoidDeadlocks = true,
            AvoidTimeouts = true,
            ReadWrite = ReadWriteDetermination.PerGrain,
            GrainOffset = 101,
        };

        var exception = await Assert.ThrowsAsync<ArgumentNullException>(
            () => grain.Run(
                options,
                depth: 1,
                stack: null!,
                maxgrain: 19,
                stopAfter: DateTime.MaxValue));

        Assert.Equal("stack", exception.ParamName);
        Assert.Equal(0, state.InteractionCount);
        Assert.Equal(0, loggerFactory.Logger.InteractionCount);
        Assert.Equal(1, loggerFactory.CreateLoggerCallCount);
        Assert.Equal("ConsistencyTestGrain.graincall", loggerFactory.LastCategoryName);
    }

    private sealed class RecordingTransactionalState : ITransactionalState<ConsistencyTestGrain.State>
    {
        public int InteractionCount { get; private set; }

        public Task<TResult> PerformRead<TResult>(Func<ConsistencyTestGrain.State, TResult> readFunction)
        {
            InteractionCount++;
            throw new InvalidOperationException("Transactional state must not be read.");
        }

        public Task<TResult> PerformUpdate<TResult>(Func<ConsistencyTestGrain.State, TResult> updateFunction)
        {
            InteractionCount++;
            throw new InvalidOperationException("Transactional state must not be updated.");
        }
    }

    private sealed class RecordingLoggerFactory : ILoggerFactory
    {
        public RecordingLogger Logger { get; } = new();

        public int CreateLoggerCallCount { get; private set; }

        public string? LastCategoryName { get; private set; }

        public void AddProvider(ILoggerProvider provider) =>
            throw new InvalidOperationException("A logger provider must not be added.");

        public ILogger CreateLogger(string categoryName)
        {
            CreateLoggerCallCount++;
            LastCategoryName = categoryName;
            return Logger;
        }

        public void Dispose()
        {
        }
    }

    private sealed class RecordingLogger : ILogger
    {
        public int InteractionCount { get; private set; }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            InteractionCount++;
            throw new InvalidOperationException("A logging scope must not be created.");
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            InteractionCount++;
            throw new InvalidOperationException("Logging must not be queried.");
        }

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            InteractionCount++;
            throw new InvalidOperationException("Logging must not occur.");
        }
    }
}
