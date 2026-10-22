using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Runtime.Scheduler;
using Xunit;

namespace UnitTests.SchedulerTests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ConsoleErrorCollection
{
    public const string Name = "Console.Error capture";
}

[Collection(ConsoleErrorCollection.Name)]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Runtime")]
[TestCategory("BVT")]
[TestCategory("Scheduler")]
public class WorkItemGroupErrorTests
{
    [Fact]
    public void LoggerDisposedAtTurnCompletion_ReportsBothErrorsAndRestoresContext()
    {
        var logger = new DisposedLogger();
        using var services = new ServiceCollection()
            .AddMetrics()
            .AddSingleton<OrleansInstruments>()
            .AddSingleton<SchedulerInstruments>()
            .AddSingleton<ILogger<WorkItemGroup>>(logger)
            .BuildServiceProvider();
        var context = Substitute.For<IGrainContext>();
        context.ActivationServices.Returns(services);
        var workGroup = new WorkItemGroup(context, Options.Create(new SchedulingOptions
        {
            // Force slow-turn reporting without depending on wall-clock timing.
            TurnWarningLengthThreshold = TimeSpan.FromMilliseconds(-1)
        }), services.GetRequiredService<SchedulerInstruments>());

        // Run Execute on the test thread, so a regression cannot kill the runner on the ThreadPool.
        var state = typeof(WorkItemGroup).GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic)!;
        state.SetValue(workGroup, Enum.Parse(state.FieldType, "Running"));
        var task = new Task(() => logger.Disposed = true);
        task.Start(workGroup.TaskScheduler);
        var originalContext = RuntimeContext.Current;
        using var emergencyOutput = new StringWriter();
        var originalError = Console.Error;
        Console.SetError(emergencyOutput);
        try
        {
            var escaped = Record.Exception(workGroup.Execute);

            Assert.Null(escaped);
            Assert.True(task.IsCompletedSuccessfully);
            Assert.Same(originalContext, RuntimeContext.Current);
            Assert.Equal(0, workGroup.ExternalWorkItemCount);
            Assert.Equal(1, logger.WarningCalls);
            Assert.Equal(1, logger.ErrorCalls);
            Assert.Contains(logger.WarningFailure.ToString(), emergencyOutput.ToString(), StringComparison.Ordinal);
            Assert.Contains(logger.ReportingFailure.ToString(), emergencyOutput.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetError(originalError);
        }
    }

    private sealed class DisposedLogger : ILogger<WorkItemGroup>
    {
        public bool Disposed { get; set; }
        public int WarningCalls { get; private set; }
        public int ErrorCalls { get; private set; }
        public Exception WarningFailure { get; } = new AggregateException(
            "An error occurred while writing to logger(s).", new ObjectDisposedException("EventLog slow-turn warning"));
        public Exception ReportingFailure { get; } = new AggregateException(
            "An error occurred while writing to logger(s).", new ObjectDisposedException("EventLog task-loop error"));

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (Disposed && logLevel == LogLevel.Warning)
            {
                WarningCalls++;
                throw WarningFailure;
            }

            if (Disposed && logLevel == LogLevel.Error)
            {
                ErrorCalls++;
                Assert.Same(WarningFailure, exception);
                throw ReportingFailure;
            }
        }
    }
}
