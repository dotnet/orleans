using System;
using Microsoft.Extensions.Logging;

namespace Orleans.Transactions.TestKit
{
    internal partial class FaultInjectionTransactionManager<TState>
        where TState : class, new()
    {
        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Grain {GrainInstance} started PrepareAndCommit transaction {TransactionId}"
        )]
        private static partial void LogInformationStartedPrepareAndCommit(ILogger logger, object grainInstance, Guid transactionId);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Grain {GrainInstance} injected fault before transaction {TransactionId} PrepareAndCommit, with fault type {FaultInjectionType}"
        )]
        private static partial void LogInformationInjectedFaultBeforePrepareAndCommit(ILogger logger, object grainInstance, Guid transactionId, FaultInjectionType faultInjectionType);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Grain {GrainInstance} deactivating after transaction {TransactionId} PrepareAndCommit"
        )]
        private static partial void LogInformationDeactivatingAfterPrepareAndCommit(ILogger logger, object grainInstance, Guid transactionId);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Grain {GrainInstance} started Prepared transaction {TransactionId}"
        )]
        private static partial void LogInformationStartedPrepared(ILogger logger, object grainInstance, Guid transactionId);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Grain {GrainInstance} deactivating after transaction {TransactionId} Prepared"
        )]
        private static partial void LogInformationDeactivatingAfterPrepared(ILogger logger, object grainInstance, Guid transactionId);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Grain {GrainInstance} started Ping transaction {TransactionId}"
        )]
        private static partial void LogInformationStartedPing(ILogger logger, object grainInstance, Guid transactionId);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Grain {GrainInstance} deactivating after transaction {TransactionId} Ping"
        )]
        private static partial void LogInformationDeactivatingAfterPing(ILogger logger, object grainInstance, Guid transactionId);
    }

    internal partial class FaultInjectionTransactionalResource<TState>
        where TState : class, new()
    {
        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Grain {GrainInstance} started CommitReadOnly transaction {TransactionId}"
        )]
        private static partial void LogInformationStartedCommitReadOnly(ILogger logger, object grainInstance, Guid transactionId);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Grain {GrainInstance} deactivating after transaction {TransactionId} CommitReadOnly"
        )]
        private static partial void LogInformationDeactivatingAfterCommitReadOnly(ILogger logger, object grainInstance, Guid transactionId);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Grain {GrainInstance} aborting transaction {TransactionId}"
        )]
        private static partial void LogInformationAborting(ILogger logger, object grainInstance, Guid transactionId);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Grain {GrainInstance} deactivating after transaction {TransactionId} abort"
        )]
        private static partial void LogInformationDeactivatingAfterAbort(ILogger logger, object grainInstance, Guid transactionId);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Grain {GrainInstance} canceling transaction {TransactionId}"
        )]
        private static partial void LogInformationCancelling(ILogger logger, object grainInstance, Guid transactionId);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Grain {GrainInstance} deactivating after transaction {TransactionId} cancel"
        )]
        private static partial void LogInformationDeactivatingAfterCancel(ILogger logger, object grainInstance, Guid transactionId);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Grain {GrainInstance} started Confirm transaction {TransactionId}"
        )]
        private static partial void LogInformationStartedConfirm(ILogger logger, object grainInstance, Guid transactionId);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Grain {GrainInstance} injected fault before transaction {TransactionId} Confirm, with fault type {FaultInjectionType}"
        )]
        private static partial void LogInformationInjectedFaultBeforeConfirm(ILogger logger, object grainInstance, Guid transactionId, FaultInjectionType faultInjectionType);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Grain {GrainInstance} deactivating after transaction {TransactionId} Confirm"
        )]
        private static partial void LogInformationDeactivatingAfterConfirm(ILogger logger, object grainInstance, Guid transactionId);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Grain {GrainInstance} started Prepare transaction {TransactionId}"
        )]
        private static partial void LogInformationStartedPrepare(ILogger logger, object grainInstance, Guid transactionId);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Grain {GrainInstance} injected fault before transaction {TransactionId} Prepare, with fault type {FaultInjectionType}"
        )]
        private static partial void LogInformationInjectedFaultBeforePrepare(ILogger logger, object grainInstance, Guid transactionId, FaultInjectionType faultInjectionType);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Grain {GrainInstance} deactivating after transaction {TransactionId} Prepare"
        )]
        private static partial void LogInformationDeactivatingAfterPrepare(ILogger logger, object grainInstance, Guid transactionId);
    }
}

namespace Orleans.Transactions.TestKit.Consistency
{
    public partial class ConsistencyTestGrain
    {
        [LoggerMessage(
            Level = LogLevel.Trace,
            Message = "g{MyNumber} {CurrentTransactionId} {Stack} Write"
        )]
        private static partial void LogTraceWrite(ILogger logger, int myNumber, object currentTransactionId, string stack);

        [LoggerMessage(
            Level = LogLevel.Trace,
            Message = "g{MyNumber} {CurrentTransactionId} {Stack} Read"
        )]
        private static partial void LogTraceRead(ILogger logger, int myNumber, object currentTransactionId, string stack);

        [LoggerMessage(
            Level = LogLevel.Trace,
            Message = "g{MyNumber} {CurrentTransactionId} {Stack} --> {ExceptionType}"
        )]
        private static partial void LogTraceException(ILogger logger, int myNumber, object currentTransactionId, string stack, string exceptionType);

        [LoggerMessage(
            Level = LogLevel.Trace,
            Message = "g{MyNumber} {CurrentTransactionId} {Stack} Recurse {Count} {ParallelOrSequential}"
        )]
        private static partial void LogTraceRecurse(ILogger logger, int myNumber, object currentTransactionId, string stack, int count, string parallelOrSequential);
    }
}
