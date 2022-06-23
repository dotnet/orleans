using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;
using Orleans.Transactions.Abstractions;
using Orleans.Transactions.State;

namespace Orleans.Transactions.TestKit
{
    internal class FaultInjectionTransactionManager<TState> : ITransactionManager
        where TState : class, new()
    {
        private TransactionManager<TState> tm;
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
            this.logger.LogInformation(
                "Grain {GrainInstance} started PrepareAndCommit transaction {TransactionId}",
                context.GrainInstance,
                transactionId);
            if (this.faultInjectionControl.FaultInjectionPhase == TransactionFaultInjectPhase.BeforePrepareAndCommit)
            {
                if (this.faultInjectionControl.FaultInjectionType == FaultInjectionType.ExceptionBeforeStore)
                    this.faultInjector.InjectBeforeStore = true;
                if (this.faultInjectionControl.FaultInjectionType == FaultInjectionType.ExceptionAfterStore)
                    this.faultInjector.InjectAfterStore = true;
                this.logger.LogInformation(
                    "Grain {GrainInstance} injected fault before transaction {TransactionId} PrepareAndCommit, with fault type {FaultInjectionType}",
                    faultInjectionControl.FaultInjectionType,
                    context.GrainInstance,
                    transactionId);
            }
            var result = await this.tm.PrepareAndCommit(transactionId, accessCount, timeStamp, writeParticipants, totalParticipants);
            if (this.faultInjectionControl?.FaultInjectionPhase == TransactionFaultInjectPhase.AfterPrepareAndCommit && this.faultInjectionControl.FaultInjectionType == FaultInjectionType.Deactivation)
            {
                this.grainRuntime.DeactivateOnIdle(context);
                this.logger.LogInformation(
                    "Grain {GrainInstance} deactivating after transaction {TransactionId} PrepareAndCommit",
                    context.GrainInstance,
                    transactionId);
            }
            this.faultInjectionControl.Reset();
            return result;
        }

        public async Task Prepared(Guid transactionId, DateTime timeStamp, ParticipantId participant, TransactionalStatus status)
        {
            this.logger.LogInformation(
                "Grain {GrainInstance} started Prepared transaction {TransactionId}",
                context.GrainInstance,
                transactionId);
            await this.tm.Prepared(transactionId, timeStamp, participant, status);
            if (this.faultInjectionControl.FaultInjectionPhase == TransactionFaultInjectPhase.AfterPrepared
                && this.faultInjectionControl?.FaultInjectionType == FaultInjectionType.Deactivation)
            {
                this.grainRuntime.DeactivateOnIdle(context);
                this.logger.LogInformation(
                    "Grain {GrainInstance} deactivating after transaction {TransactionId} Prepared",
                    context.GrainInstance,
                    transactionId);
            }
            this.faultInjectionControl.Reset();
        }

        public async Task Ping(Guid transactionId, DateTime timeStamp, ParticipantId participant)
        {
            this.logger.LogInformation("Grain {GrainInstance} started Ping transaction {TransactionId}", context.GrainInstance, transactionId);
            await this.tm.Ping(transactionId, timeStamp, participant);
            if (this.faultInjectionControl?.FaultInjectionPhase == TransactionFaultInjectPhase.AfterPing
                && this.faultInjectionControl.FaultInjectionType == FaultInjectionType.Deactivation)
            {
                this.grainRuntime.DeactivateOnIdle(context);
                this.logger.LogInformation(
                    "Grain {GrainInstance} deactivating after transaction {TransactionId} Ping",
                    context.GrainInstance,
                    transactionId);
            }
            this.faultInjectionControl.Reset();
        }

    }

    internal class FaultInjectionTransactionalResource<TState> : ITransactionalResource
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
            this.logger.LogInformation(
                "Grain {GrainInstance} started CommitReadOnly transaction {TransactionId}",
                context.GrainInstance,
                transactionId);
            var result = await this.tResource.CommitReadOnly(transactionId, accessCount, timeStamp);
            if (this.faultInjectionControl.FaultInjectionPhase == TransactionFaultInjectPhase.AfterCommitReadOnly
                && this.faultInjectionControl.FaultInjectionType == FaultInjectionType.Deactivation)
            {
                this.grainRuntime.DeactivateOnIdle(context);
                this.logger.LogInformation(
                    "Grain {GrainInstance} deactivating after transaction {TransactionId} CommitReadOnly",
                    context.GrainInstance,
                    transactionId);
            }

            this.faultInjectionControl.Reset();
            return result;
        }

        public async Task Abort(Guid transactionId)
        {
            this.logger.LogInformation(
                "Grain {GrainInstance} aborting transaction {TransactionId}",
                context.GrainInstance,
                transactionId);
            await this.tResource.Abort(transactionId);
            if (this.faultInjectionControl.FaultInjectionPhase == TransactionFaultInjectPhase.AfterAbort
                && this.faultInjectionControl.FaultInjectionType == FaultInjectionType.Deactivation)
            {
                this.grainRuntime.DeactivateOnIdle(context);
                this.logger.LogInformation(
                    "Grain {GrainInstance} deactivating after transaction {TransactionId} abort",
                    context.GrainInstance,
                    transactionId);
            }
            this.faultInjectionControl.Reset();
        }

        public async Task Cancel(Guid transactionId, DateTime timeStamp, TransactionalStatus status)
        {
            this.logger.LogInformation("Grain {GrainInstance} canceling transaction {TransactionId}", context.GrainInstance, transactionId);
            await this.tResource.Cancel(transactionId, timeStamp, status);
            if (this.faultInjectionControl.FaultInjectionPhase == TransactionFaultInjectPhase.AfterCancel
                && this.faultInjectionControl.FaultInjectionType == FaultInjectionType.Deactivation)
            {
                this.grainRuntime.DeactivateOnIdle(context);
                this.logger.LogInformation(
                    "Grain {GrainInstance} deactivating after transaction {TransactionId} cancel",
                    context.GrainInstance,
                    transactionId);
            }
            this.faultInjectionControl.Reset();
        }

        public async Task Confirm(Guid transactionId, DateTime timeStamp)
        {
            this.logger.LogInformation(
                "Grain {GrainInstance} started Confirm transaction {TransactionId}",
                context.GrainInstance,
                transactionId);
            if (this.faultInjectionControl?.FaultInjectionPhase == TransactionFaultInjectPhase.BeforeConfirm)
            {
                if (this.faultInjectionControl.FaultInjectionType == FaultInjectionType.ExceptionBeforeStore)
                    this.faultInjector.InjectBeforeStore = true;
                if (this.faultInjectionControl.FaultInjectionType == FaultInjectionType.ExceptionAfterStore)
                    this.faultInjector.InjectAfterStore = true;
                this.logger.LogInformation(
                    "Grain {GrainInstance} injected fault before transaction {TransactionId} Confirm, with fault type {FaultInjectionType}",
                    faultInjectionControl.FaultInjectionType,
                    context.GrainInstance,
                    transactionId);
            }
            await this.tResource.Confirm(transactionId, timeStamp);
            if (this.faultInjectionControl.FaultInjectionPhase == TransactionFaultInjectPhase.AfterConfirm
                && this.faultInjectionControl.FaultInjectionType == FaultInjectionType.Deactivation)
            {
                this.grainRuntime.DeactivateOnIdle(context);
                this.logger.LogInformation(
                    "Grain {GrainInstance} deactivating after transaction {TransactionId} Confirm",
                    context.GrainInstance,
                    transactionId);
            }
            this.faultInjectionControl.Reset();
        }

        public async Task Prepare(Guid transactionId, AccessCounter accessCount, DateTime timeStamp, ParticipantId transactionManager)
        {
            this.logger.LogInformation(
                "Grain {GrainInstance} started Prepare transaction {TransactionId}",
                context.GrainInstance,
                transactionId);

            if (this.faultInjectionControl?.FaultInjectionPhase == TransactionFaultInjectPhase.BeforePrepare)
            {
                if (this.faultInjectionControl.FaultInjectionType == FaultInjectionType.ExceptionBeforeStore)
                    this.faultInjector.InjectBeforeStore = true;
                if (this.faultInjectionControl.FaultInjectionType == FaultInjectionType.ExceptionAfterStore)
                    this.faultInjector.InjectAfterStore = true;
                this.logger.LogInformation(
                    "Grain {GrainInstance} injected fault before transaction {TransactionId} Prepare, with fault type {FaultInjectionType}",
                    this.context.GrainInstance,
                    transactionId,
                    faultInjectionControl.FaultInjectionType);
            }

            await this.tResource.Prepare(transactionId, accessCount, timeStamp, transactionManager);
            if (this.faultInjectionControl.FaultInjectionPhase == TransactionFaultInjectPhase.AfterPrepare
                && this.faultInjectionControl.FaultInjectionType == FaultInjectionType.Deactivation)
            {
                this.grainRuntime.DeactivateOnIdle(context);
                this.logger.LogInformation("Grain {GrainInstance} deactivating after transaction {TransactionId} Prepare", this.context.GrainInstance, transactionId);
            }
            this.faultInjectionControl.Reset();
        }
    }
}
