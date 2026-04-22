using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Orleans.Runtime
{
    internal sealed partial class ActivationData
    {
        [LoggerMessage(
            Level = LogLevel.Error,
            Message = "Error in grain message loop"
        )]
        private static partial void LogErrorInGrainMessageLoop(ILogger logger, Exception exception);
    }
}

namespace Orleans.Runtime.MembershipService
{
    internal partial class MembershipAgent
    {
        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "Graceful shutdown aborted: starting ungraceful shutdown"
        )]
        private static partial void LogWarningGracefulShutdownAborted(ILogger logger);
    }
}

namespace Orleans.Runtime.Scheduler
{
    internal sealed partial class WorkItemGroup
    {
        [LoggerMessage(
            Level = LogLevel.Trace,
            Message = "EnqueueWorkItem {Task} into {GrainContext} when TaskScheduler.Current={TaskScheduler}"
        )]
        private static partial void LogTraceEnqueueWorkItem(ILogger logger, Task task, IGrainContext grainContext, TaskScheduler taskScheduler);

        [LoggerMessage(
            Level = LogLevel.Trace,
            Message = "Add to RunQueue {Task}, #{SequenceNumber}, onto {GrainContext}"
        )]
        private static partial void LogTraceAddToRunQueue(ILogger logger, Task task, long sequenceNumber, IGrainContext grainContext);

        [LoggerMessage(
            EventId = (int)ErrorCode.SchedulerTooManyPendingItems,
            Level = LogLevel.Warning,
            Message = "{PendingWorkItemCount} pending work items for group {WorkGroupName}, exceeding the warning threshold of {WarningThreshold}"
        )]
        private static partial void LogWarningTooManyPendingItems(ILogger logger, int pendingWorkItemCount, string workGroupName, int warningThreshold);

        [LoggerMessage(
            Level = LogLevel.Trace,
            Message = "About to execute task '{Task}' in GrainContext={GrainContext}"
        )]
        private static partial void LogTraceAboutToExecuteTask(ILogger logger, Task task, IGrainContext grainContext);

        [LoggerMessage(
            EventId = (int)ErrorCode.Runtime_Error_100032,
            Level = LogLevel.Error,
            Message = "Worker thread {Thread} caught an exception thrown from IWorkItem.Execute"
        )]
        private static partial void LogErrorTaskLoop(ILogger logger, Exception exception, int thread);

        [LoggerMessage(
            EventId = (int)ErrorCode.SchedulerTurnTooLong3,
            Level = LogLevel.Warning,
            Message = "Task {Task} in WorkGroup {GrainContext} took elapsed time {Duration} for execution, which is longer than {TurnWarningLengthThreshold}. Running on thread {Thread}"
        )]
        private static partial void LogWarningLongRunningTurn(ILogger logger, object task, string grainContext, string duration, TimeSpan turnWarningLengthThreshold, string thread);
    }
}

namespace Orleans.Runtime.GrainDirectory
{
    internal sealed partial class DistributedGrainDirectory
    {
        [LoggerMessage(
            Level = LogLevel.Trace,
            Message = "Invoking '{Operation}' on '{Owner}' for grain '{GrainId}'."
        )]
        private static partial void LogTraceInvokingOperation(ILogger logger, string operation, SiloAddress owner, GrainId grainId);
    }
}
