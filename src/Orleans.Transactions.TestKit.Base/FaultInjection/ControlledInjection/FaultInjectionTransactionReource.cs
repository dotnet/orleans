using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;
using Orleans.Transactions.Abstractions;
using Orleans.Transactions.State;

#nullable disable
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
            if (this.faultInjectionControl.FaultInjectionPhase == TransactionFaultInjectPhase.BeforePrepareAndCommit)
            {
                if (this.faultInjectionControl.FaultInjectionType == FaultInjectionType.ExceptionBeforeStore)
                    this.faultInjector.InjectBeforeStore = true;
                if (this.faultInjectionControl.FaultInjectionType == FaultInjectionType.ExceptionAfterStore)
                    this.faultInjector.InjectAfterStore = true;
                LogInformationInjectedFaultBeforePrepareAndCommit(this.logger, context.GrainInstance, transactionId, faultInjectionControl.FaultInjectionType);
            }
            var result = await this.tm.PrepareAndCommit(transactionId, accessCount, timeStamp, writeParticipants, totalParticipants);
            if (this.faultInjectionControl?.FaultInjectionPhase == TransactionFaultInjectPhase.AfterPrepareAndCommit && this.faultInjectionControl.FaultInjectionType == FaultInjectionType.Deactivation)
            {
                this.grainRuntime.DeactivateOnIdle(context);
                LogInformationDeactivatingAfterPrepareAndCommit(this.logger, context.GrainInstance, transactionId);
            }
            this.faultInjectionControl.Reset();
            return result;
        }

        public async Task Prepared(Guid transactionId, DateTime timeStamp, ParticipantId participant, TransactionalStatus status)
        {
            LogInformationStartedPrepared(this.logger, context.GrainInstance, transactionId);
            await this.tm.Prepared(transactionId, timeStamp, participant, status);
            if (this.faultInjectionControl.FaultInjectionPhase == TransactionFaultInjectPhase.AfterPrepared
                && this.faultInjectionControl?.FaultInjectionType == FaultInjectionType.Deactivation)
            {
                this.grainRuntime.DeactivateOnIdle(context);
                LogInformationDeactivatingAfterPrepared(this.logger, context.GrainInstance, transactionId);
            }
            this.faultInjectionControl.Reset();
        }

        public async Task Ping(Guid transactionId, DateTime timeStamp, ParticipantId participant)
        {
            LogInformationStartedPing(this.logger, context.GrainInstance, transactionId);
            await this.tm.Ping(transactionId, timeStamp, participant);
            if (this.faultInjectionControl?.FaultInjectionPhase == TransactionFaultInjectPhase.AfterPing
                && this.faultInjectionControl.FaultInjectionType == FaultInjectionType.Deactivation)
            {
                this.grainRuntime.DeactivateOnIdle(context);
                LogInformationDeactivatingAfterPing(this.logger, context.GrainInstance, transactionId);
            }
            this.faultInjectionControl.Reset();
        }

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
            if (this.faultInjectionControl.FaultInjectionPhase == TransactionFaultInjectPhase.AfterCommitReadOnly
                && this.faultInjectionControl.FaultInjectionType == FaultInjectionType.Deactivation)
            {
                this.grainRuntime.DeactivateOnIdle(context);
                LogInformationDeactivatingAfterCommitReadOnly(this.logger, context.GrainInstance, transactionId);
            }

            this.faultInjectionControl.Reset();
            return result;
        }

        public async Task Abort(Guid transactionId)
        {
            LogInformationAborting(this.logger, context.GrainInstance, transactionId);
            await this.tResource.Abort(transactionId);
            if (this.faultInjectionControl.FaultInjectionPhase == TransactionFaultInjectPhase.AfterAbort
                && this.faultInjectionControl.FaultInjectionType == FaultInjectionType.Deactivation)
            {
                this.grainRuntime.DeactivateOnIdle(context);
                LogInformationDeactivatingAfterAbort(this.logger, context.GrainInstance, transactionId);
            }
            this.faultInjectionControl.Reset();
        }

        public async Task Cancel(Guid transactionId, DateTime timeStamp, TransactionalStatus status)
        {
            LogInformationCancelling(this.logger, context.GrainInstance, transactionId);
            await this.tResource.Cancel(transactionId, timeStamp, status);
            if (this.faultInjectionControl.FaultInjectionPhase == TransactionFaultInjectPhase.AfterCancel
                && this.faultInjectionControl.FaultInjectionType == FaultInjectionType.Deactivation)
            {
                this.grainRuntime.DeactivateOnIdle(context);
                LogInformationDeactivatingAfterCancel(this.logger, context.GrainInstance, transactionId);
            }
            this.faultInjectionControl.Reset();
        }

        public async Task Confirm(Guid transactionId, DateTime timeStamp)
        {
            LogInformationStartedConfirm(this.logger, context.GrainInstance, transactionId);
            if (this.faultInjectionControl?.FaultInjectionPhase == TransactionFaultInjectPhase.BeforeConfirm)
            {
                if (this.faultInjectionControl.FaultInjectionType == FaultInjectionType.ExceptionBeforeStore)
                    this.faultInjector.InjectBeforeStore = true;
                if (this.faultInjectionControl.FaultInjectionType == FaultInjectionType.ExceptionAfterStore)
                    this.faultInjector.InjectAfterStore = true;
                LogInformationInjectedFaultBeforeConfirm(this.logger, context.GrainInstance, transactionId, faultInjectionControl.FaultInjectionType);
            }
            await this.tResource.Confirm(transactionId, timeStamp);
            if (this.faultInjectionControl.FaultInjectionPhase == TransactionFaultInjectPhase.AfterConfirm
                && this.faultInjectionControl.FaultInjectionType == FaultInjectionType.Deactivation)
            {
                this.grainRuntime.DeactivateOnIdle(context);
                LogInformationDeactivatingAfterConfirm(this.logger, context.GrainInstance, transactionId);
            }
            this.faultInjectionControl.Reset();
        }

        public async Task Prepare(Guid transactionId, AccessCounter accessCount, DateTime timeStamp, ParticipantId transactionManager)
        {
            LogInformationStartedPrepare(this.logger, context.GrainInstance, transactionId);

            if (this.faultInjectionControl?.FaultInjectionPhase == TransactionFaultInjectPhase.BeforePrepare)
            {
                if (this.faultInjectionControl.FaultInjectionType == FaultInjectionType.ExceptionBeforeStore)
                    this.faultInjector.InjectBeforeStore = true;
                if (this.faultInjectionControl.FaultInjectionType == FaultInjectionType.ExceptionAfterStore)
                    this.faultInjector.InjectAfterStore = true;
                LogInformationInjectedFaultBeforePrepare(this.logger, this.context.GrainInstance, transactionId, faultInjectionControl.FaultInjectionType);
            }

            await this.tResource.Prepare(transactionId, accessCount, timeStamp, transactionManager);
            if (this.faultInjectionControl.FaultInjectionPhase == TransactionFaultInjectPhase.AfterPrepare
                && this.faultInjectionControl.FaultInjectionType == FaultInjectionType.Deactivation)
            {
                this.grainRuntime.DeactivateOnIdle(context);
                LogInformationDeactivatingAfterPrepare(this.logger, this.context.GrainInstance, transactionId);
            }
            this.faultInjectionControl.Reset();
        }

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
