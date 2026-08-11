using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;
using Orleans.Transactions.Abstractions;
using Orleans.Transactions.State;

namespace Orleans.Transactions.TestKit
{
    internal partial class FaultInjectionTransactionManager<TState> : ITransactionManager
        where TState : class, new()
    {
        private readonly TransactionManager<TState> tm;
        private readonly IGrainRuntime grainRuntime;
        private readonly IGrainContext context;
        private readonly FaultInjectionControl faultInjectionControl;
        private readonly ILogger logger;
        private readonly IControlledTransactionFaultInjector faultInjector;
        public FaultInjectionTransactionManager(IControlledTransactionFaultInjector faultInjector, FaultInjectionControl faultInjectionControl, TransactionManager<TState> tm, IGrainContext activationContext, ILogger logger, IGrainRuntime grainRuntime)
        {
            this.grainRuntime = grainRuntime;
            this.tm = tm;
            this.faultInjectionControl = faultInjectionControl;
            this.logger = logger;
            this.context = activationContext;
            this.faultInjector = faultInjector;
        }

        public async Task<TransactionalStatus> PrepareAndCommit(Guid transactionId, AccessCounter accessCount, DateTime timeStamp, List<ParticipantId> writeParticipants, int totalParticipants)
        {
            LogInformationStartedPrepareAndCommit(this.logger, context.GrainInstance, transactionId);
            var injectBeforeStore = this.faultInjectionControl.TryConsume(
                TransactionFaultInjectPhase.BeforePrepareAndCommit,
                out var injectionType);
            if (injectBeforeStore)
            {
                if (injectionType == FaultInjectionType.ExceptionBeforeStore)
                    this.faultInjector.InjectBeforeStore = true;
                if (injectionType == FaultInjectionType.ExceptionAfterStore)
                    this.faultInjector.InjectAfterStore = true;
                if (injectionType == FaultInjectionType.GenericExceptionAfterStore)
                    this.faultInjector.InjectGenericAfterStore = true;
                LogInformationInjectedFaultBeforePrepareAndCommit(this.logger, context.GrainInstance, transactionId, injectionType);
                FaultInjectionDiagnosticEvents.Emit(new(
                    this.context.GrainId,
                    transactionId,
                    TransactionFaultInjectPhase.BeforePrepareAndCommit,
                    injectionType));
            }
            var result = await this.tm.PrepareAndCommit(transactionId, accessCount, timeStamp, writeParticipants, totalParticipants);
            if (this.faultInjectionControl.TryConsume(
                    TransactionFaultInjectPhase.AfterPrepareAndCommit,
                    out injectionType)
                && injectionType == FaultInjectionType.Deactivation)
            {
                FaultInjectionDiagnosticEvents.Emit(new(
                    this.context.GrainId,
                    transactionId,
                    TransactionFaultInjectPhase.AfterPrepareAndCommit,
                    injectionType));
                this.grainRuntime.DeactivateOnIdle(context);
                LogInformationDeactivatingAfterPrepareAndCommit(this.logger, context.GrainInstance, transactionId);
            }
            return result;
        }

        public async Task Prepared(Guid transactionId, DateTime timeStamp, ParticipantId participant, TransactionalStatus status)
        {
            LogInformationStartedPrepared(this.logger, context.GrainInstance, transactionId);
            await this.tm.Prepared(transactionId, timeStamp, participant, status);
            if (this.faultInjectionControl.TryConsume(
                    TransactionFaultInjectPhase.AfterPrepared,
                    out var injectionType)
                && injectionType == FaultInjectionType.Deactivation)
            {
                FaultInjectionDiagnosticEvents.Emit(new(
                    this.context.GrainId,
                    transactionId,
                    TransactionFaultInjectPhase.AfterPrepared,
                    injectionType));
                this.grainRuntime.DeactivateOnIdle(context);
                LogInformationDeactivatingAfterPrepared(this.logger, context.GrainInstance, transactionId);
            }
        }

        public async Task Ping(Guid transactionId, DateTime timeStamp, ParticipantId participant)
        {
            LogInformationStartedPing(this.logger, context.GrainInstance, transactionId);
            await this.tm.Ping(transactionId, timeStamp, participant);
            if (this.faultInjectionControl.TryConsume(
                    TransactionFaultInjectPhase.AfterPing,
                    out var injectionType)
                && injectionType == FaultInjectionType.Deactivation)
            {
                FaultInjectionDiagnosticEvents.Emit(new(
                    this.context.GrainId,
                    transactionId,
                    TransactionFaultInjectPhase.AfterPing,
                    injectionType));
                this.grainRuntime.DeactivateOnIdle(context);
                LogInformationDeactivatingAfterPing(this.logger, context.GrainInstance, transactionId);
            }
        }

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Grain {GrainInstance} started PrepareAndCommit transaction {TransactionId}"
        )]
        private static partial void LogInformationStartedPrepareAndCommit(ILogger logger, object? grainInstance, Guid transactionId);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Grain {GrainInstance} injected fault before transaction {TransactionId} PrepareAndCommit, with fault type {FaultInjectionType}"
        )]
        private static partial void LogInformationInjectedFaultBeforePrepareAndCommit(ILogger logger, object? grainInstance, Guid transactionId, FaultInjectionType faultInjectionType);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Grain {GrainInstance} deactivating after transaction {TransactionId} PrepareAndCommit"
        )]
        private static partial void LogInformationDeactivatingAfterPrepareAndCommit(ILogger logger, object? grainInstance, Guid transactionId);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Grain {GrainInstance} started Prepared transaction {TransactionId}"
        )]
        private static partial void LogInformationStartedPrepared(ILogger logger, object? grainInstance, Guid transactionId);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Grain {GrainInstance} deactivating after transaction {TransactionId} Prepared"
        )]
        private static partial void LogInformationDeactivatingAfterPrepared(ILogger logger, object? grainInstance, Guid transactionId);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Grain {GrainInstance} started Ping transaction {TransactionId}"
        )]
        private static partial void LogInformationStartedPing(ILogger logger, object? grainInstance, Guid transactionId);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Grain {GrainInstance} deactivating after transaction {TransactionId} Ping"
        )]
        private static partial void LogInformationDeactivatingAfterPing(ILogger logger, object? grainInstance, Guid transactionId);

    }

    internal partial class FaultInjectionTransactionalResource<TState> : ITransactionalResource
        where TState : class, new()
    {

        private readonly IGrainRuntime grainRuntime;
        private readonly IGrainContext context;
        private readonly FaultInjectionControl faultInjectionControl;
        private readonly TransactionalResource<TState> tResource;
        private readonly IControlledTransactionFaultInjector faultInjector;
        private readonly ILogger logger;
        public FaultInjectionTransactionalResource(IControlledTransactionFaultInjector faultInjector, FaultInjectionControl faultInjectionControl, 
            TransactionalResource<TState> tResource, IGrainContext activationContext, ILogger logger, IGrainRuntime grainRuntime)
        {
            this.grainRuntime = grainRuntime;
            this.tResource = tResource;
            this.faultInjectionControl = faultInjectionControl;
            this.logger = logger;
            this.faultInjector = faultInjector;
            this.context = activationContext;
        }

        public async Task<TransactionalStatus> CommitReadOnly(Guid transactionId, AccessCounter accessCount, DateTime timeStamp)
        {
            LogInformationStartedCommitReadOnly(this.logger, context.GrainInstance, transactionId);
            var result = await this.tResource.CommitReadOnly(transactionId, accessCount, timeStamp);
            if (this.faultInjectionControl.TryConsume(
                    TransactionFaultInjectPhase.AfterCommitReadOnly,
                    out var injectionType)
                && injectionType == FaultInjectionType.Deactivation)
            {
                FaultInjectionDiagnosticEvents.Emit(new(
                    this.context.GrainId,
                    transactionId,
                    TransactionFaultInjectPhase.AfterCommitReadOnly,
                    injectionType));
                this.grainRuntime.DeactivateOnIdle(context);
                LogInformationDeactivatingAfterCommitReadOnly(this.logger, context.GrainInstance, transactionId);
            }

            return result;
        }

        public async Task Abort(Guid transactionId)
        {
            LogInformationAborting(this.logger, context.GrainInstance, transactionId);
            await this.tResource.Abort(transactionId);
            if (this.faultInjectionControl.TryConsume(
                    TransactionFaultInjectPhase.AfterAbort,
                    out var injectionType)
                && injectionType == FaultInjectionType.Deactivation)
            {
                FaultInjectionDiagnosticEvents.Emit(new(
                    this.context.GrainId,
                    transactionId,
                    TransactionFaultInjectPhase.AfterAbort,
                    injectionType));
                this.grainRuntime.DeactivateOnIdle(context);
                LogInformationDeactivatingAfterAbort(this.logger, context.GrainInstance, transactionId);
            }
        }

        public async Task Cancel(Guid transactionId, DateTime timeStamp, TransactionalStatus status)
        {
            LogInformationCancelling(this.logger, context.GrainInstance, transactionId);
            await this.tResource.Cancel(transactionId, timeStamp, status);
            if (this.faultInjectionControl.TryConsume(
                    TransactionFaultInjectPhase.AfterCancel,
                    out var injectionType)
                && injectionType == FaultInjectionType.Deactivation)
            {
                FaultInjectionDiagnosticEvents.Emit(new(
                    this.context.GrainId,
                    transactionId,
                    TransactionFaultInjectPhase.AfterCancel,
                    injectionType));
                this.grainRuntime.DeactivateOnIdle(context);
                LogInformationDeactivatingAfterCancel(this.logger, context.GrainInstance, transactionId);
            }
        }

        public async Task Confirm(Guid transactionId, DateTime timeStamp)
        {
            LogInformationStartedConfirm(this.logger, context.GrainInstance, transactionId);
            var injectBeforeStore = this.faultInjectionControl.TryConsume(
                TransactionFaultInjectPhase.BeforeConfirm,
                out var injectionType);
            if (injectBeforeStore)
            {
                if (injectionType == FaultInjectionType.ExceptionBeforeStore)
                    this.faultInjector.InjectBeforeStore = true;
                if (injectionType == FaultInjectionType.ExceptionAfterStore)
                    this.faultInjector.InjectAfterStore = true;
                if (injectionType == FaultInjectionType.GenericExceptionAfterStore)
                    this.faultInjector.InjectGenericAfterStore = true;
                LogInformationInjectedFaultBeforeConfirm(this.logger, context.GrainInstance, transactionId, injectionType);
                FaultInjectionDiagnosticEvents.Emit(new(
                    this.context.GrainId,
                    transactionId,
                    TransactionFaultInjectPhase.BeforeConfirm,
                    injectionType));
            }
            await this.tResource.Confirm(transactionId, timeStamp);
            if (this.faultInjectionControl.TryConsume(
                    TransactionFaultInjectPhase.AfterConfirm,
                    out injectionType)
                && injectionType == FaultInjectionType.Deactivation)
            {
                FaultInjectionDiagnosticEvents.Emit(new(
                    this.context.GrainId,
                    transactionId,
                    TransactionFaultInjectPhase.AfterConfirm,
                    injectionType));
                this.grainRuntime.DeactivateOnIdle(context);
                LogInformationDeactivatingAfterConfirm(this.logger, context.GrainInstance, transactionId);
            }
        }

        public async Task Prepare(Guid transactionId, AccessCounter accessCount, DateTime timeStamp, ParticipantId transactionManager)
        {
            LogInformationStartedPrepare(this.logger, context.GrainInstance, transactionId);

            var injectBeforeStore = this.faultInjectionControl.TryConsume(
                TransactionFaultInjectPhase.BeforePrepare,
                out var injectionType);
            if (injectBeforeStore)
            {
                if (injectionType == FaultInjectionType.ExceptionBeforeStore)
                    this.faultInjector.InjectBeforeStore = true;
                if (injectionType == FaultInjectionType.ExceptionAfterStore)
                    this.faultInjector.InjectAfterStore = true;
                if (injectionType == FaultInjectionType.GenericExceptionAfterStore)
                    this.faultInjector.InjectGenericAfterStore = true;
                LogInformationInjectedFaultBeforePrepare(this.logger, this.context.GrainInstance, transactionId, injectionType);
                FaultInjectionDiagnosticEvents.Emit(new(
                    this.context.GrainId,
                    transactionId,
                    TransactionFaultInjectPhase.BeforePrepare,
                    injectionType));
            }

            await this.tResource.Prepare(transactionId, accessCount, timeStamp, transactionManager);
            if (this.faultInjectionControl.TryConsume(
                    TransactionFaultInjectPhase.AfterPrepare,
                    out injectionType)
                && injectionType == FaultInjectionType.Deactivation)
            {
                FaultInjectionDiagnosticEvents.Emit(new(
                    this.context.GrainId,
                    transactionId,
                    TransactionFaultInjectPhase.AfterPrepare,
                    injectionType));
                this.grainRuntime.DeactivateOnIdle(context);
                LogInformationDeactivatingAfterPrepare(this.logger, this.context.GrainInstance, transactionId);
            }
        }

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Grain {GrainInstance} started CommitReadOnly transaction {TransactionId}"
        )]
        private static partial void LogInformationStartedCommitReadOnly(ILogger logger, object? grainInstance, Guid transactionId);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Grain {GrainInstance} deactivating after transaction {TransactionId} CommitReadOnly"
        )]
        private static partial void LogInformationDeactivatingAfterCommitReadOnly(ILogger logger, object? grainInstance, Guid transactionId);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Grain {GrainInstance} aborting transaction {TransactionId}"
        )]
        private static partial void LogInformationAborting(ILogger logger, object? grainInstance, Guid transactionId);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Grain {GrainInstance} deactivating after transaction {TransactionId} abort"
        )]
        private static partial void LogInformationDeactivatingAfterAbort(ILogger logger, object? grainInstance, Guid transactionId);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Grain {GrainInstance} canceling transaction {TransactionId}"
        )]
        private static partial void LogInformationCancelling(ILogger logger, object? grainInstance, Guid transactionId);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Grain {GrainInstance} deactivating after transaction {TransactionId} cancel"
        )]
        private static partial void LogInformationDeactivatingAfterCancel(ILogger logger, object? grainInstance, Guid transactionId);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Grain {GrainInstance} started Confirm transaction {TransactionId}"
        )]
        private static partial void LogInformationStartedConfirm(ILogger logger, object? grainInstance, Guid transactionId);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Grain {GrainInstance} injected fault before transaction {TransactionId} Confirm, with fault type {FaultInjectionType}"
        )]
        private static partial void LogInformationInjectedFaultBeforeConfirm(ILogger logger, object? grainInstance, Guid transactionId, FaultInjectionType faultInjectionType);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Grain {GrainInstance} deactivating after transaction {TransactionId} Confirm"
        )]
        private static partial void LogInformationDeactivatingAfterConfirm(ILogger logger, object? grainInstance, Guid transactionId);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Grain {GrainInstance} started Prepare transaction {TransactionId}"
        )]
        private static partial void LogInformationStartedPrepare(ILogger logger, object? grainInstance, Guid transactionId);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Grain {GrainInstance} injected fault before transaction {TransactionId} Prepare, with fault type {FaultInjectionType}"
        )]
        private static partial void LogInformationInjectedFaultBeforePrepare(ILogger logger, object? grainInstance, Guid transactionId, FaultInjectionType faultInjectionType);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Grain {GrainInstance} deactivating after transaction {TransactionId} Prepare"
        )]
        private static partial void LogInformationDeactivatingAfterPrepare(ILogger logger, object? grainInstance, Guid transactionId);
    }
}
