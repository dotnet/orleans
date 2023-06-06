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
            context = activationContext;
            this.faultInjector = faultInjector;
        }

        public async Task<TransactionalStatus> PrepareAndCommit(Guid transactionId, AccessCounter accessCount, DateTime timeStamp, List<ParticipantId> writeParticipants, int totalParticipants)
        {
            logger.LogInformation(
                "Grain {GrainInstance} started PrepareAndCommit transaction {TransactionId}",
                context.GrainInstance,
                transactionId);
            if (faultInjectionControl.FaultInjectionPhase == TransactionFaultInjectPhase.BeforePrepareAndCommit)
            {
                if (faultInjectionControl.FaultInjectionType == FaultInjectionType.ExceptionBeforeStore)
                    faultInjector.InjectBeforeStore = true;
                if (faultInjectionControl.FaultInjectionType == FaultInjectionType.ExceptionAfterStore)
                    faultInjector.InjectAfterStore = true;
                logger.LogInformation(
                    "Grain {GrainInstance} injected fault before transaction {TransactionId} PrepareAndCommit, with fault type {FaultInjectionType}",
                    faultInjectionControl.FaultInjectionType,
                    context.GrainInstance,
                    transactionId);
            }
            var result = await tm.PrepareAndCommit(transactionId, accessCount, timeStamp, writeParticipants, totalParticipants);
            if (faultInjectionControl?.FaultInjectionPhase == TransactionFaultInjectPhase.AfterPrepareAndCommit && faultInjectionControl.FaultInjectionType == FaultInjectionType.Deactivation)
            {
                grainRuntime.DeactivateOnIdle(context);
                logger.LogInformation(
                    "Grain {GrainInstance} deactivating after transaction {TransactionId} PrepareAndCommit",
                    context.GrainInstance,
                    transactionId);
            }
            faultInjectionControl.Reset();
            return result;
        }

        public async Task Prepared(Guid transactionId, DateTime timeStamp, ParticipantId participant, TransactionalStatus status)
        {
            logger.LogInformation(
                "Grain {GrainInstance} started Prepared transaction {TransactionId}",
                context.GrainInstance,
                transactionId);
            await tm.Prepared(transactionId, timeStamp, participant, status);
            if (faultInjectionControl.FaultInjectionPhase == TransactionFaultInjectPhase.AfterPrepared
                && faultInjectionControl?.FaultInjectionType == FaultInjectionType.Deactivation)
            {
                grainRuntime.DeactivateOnIdle(context);
                logger.LogInformation(
                    "Grain {GrainInstance} deactivating after transaction {TransactionId} Prepared",
                    context.GrainInstance,
                    transactionId);
            }
            faultInjectionControl.Reset();
        }

        public async Task Ping(Guid transactionId, DateTime timeStamp, ParticipantId participant)
        {
            logger.LogInformation("Grain {GrainInstance} started Ping transaction {TransactionId}", context.GrainInstance, transactionId);
            await tm.Ping(transactionId, timeStamp, participant);
            if (faultInjectionControl?.FaultInjectionPhase == TransactionFaultInjectPhase.AfterPing
                && faultInjectionControl.FaultInjectionType == FaultInjectionType.Deactivation)
            {
                grainRuntime.DeactivateOnIdle(context);
                logger.LogInformation(
                    "Grain {GrainInstance} deactivating after transaction {TransactionId} Ping",
                    context.GrainInstance,
                    transactionId);
            }
            faultInjectionControl.Reset();
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
            context = activationContext;
        }

        public async Task<TransactionalStatus> CommitReadOnly(Guid transactionId, AccessCounter accessCount, DateTime timeStamp)
        {
            logger.LogInformation(
                "Grain {GrainInstance} started CommitReadOnly transaction {TransactionId}",
                context.GrainInstance,
                transactionId);
            var result = await tResource.CommitReadOnly(transactionId, accessCount, timeStamp);
            if (faultInjectionControl.FaultInjectionPhase == TransactionFaultInjectPhase.AfterCommitReadOnly
                && faultInjectionControl.FaultInjectionType == FaultInjectionType.Deactivation)
            {
                grainRuntime.DeactivateOnIdle(context);
                logger.LogInformation(
                    "Grain {GrainInstance} deactivating after transaction {TransactionId} CommitReadOnly",
                    context.GrainInstance,
                    transactionId);
            }

            faultInjectionControl.Reset();
            return result;
        }

        public async Task Abort(Guid transactionId)
        {
            logger.LogInformation(
                "Grain {GrainInstance} aborting transaction {TransactionId}",
                context.GrainInstance,
                transactionId);
            await tResource.Abort(transactionId);
            if (faultInjectionControl.FaultInjectionPhase == TransactionFaultInjectPhase.AfterAbort
                && faultInjectionControl.FaultInjectionType == FaultInjectionType.Deactivation)
            {
                grainRuntime.DeactivateOnIdle(context);
                logger.LogInformation(
                    "Grain {GrainInstance} deactivating after transaction {TransactionId} abort",
                    context.GrainInstance,
                    transactionId);
            }
            faultInjectionControl.Reset();
        }

        public async Task Cancel(Guid transactionId, DateTime timeStamp, TransactionalStatus status)
        {
            logger.LogInformation("Grain {GrainInstance} canceling transaction {TransactionId}", context.GrainInstance, transactionId);
            await tResource.Cancel(transactionId, timeStamp, status);
            if (faultInjectionControl.FaultInjectionPhase == TransactionFaultInjectPhase.AfterCancel
                && faultInjectionControl.FaultInjectionType == FaultInjectionType.Deactivation)
            {
                grainRuntime.DeactivateOnIdle(context);
                logger.LogInformation(
                    "Grain {GrainInstance} deactivating after transaction {TransactionId} cancel",
                    context.GrainInstance,
                    transactionId);
            }
            faultInjectionControl.Reset();
        }

        public async Task Confirm(Guid transactionId, DateTime timeStamp)
        {
            logger.LogInformation(
                "Grain {GrainInstance} started Confirm transaction {TransactionId}",
                context.GrainInstance,
                transactionId);
            if (faultInjectionControl?.FaultInjectionPhase == TransactionFaultInjectPhase.BeforeConfirm)
            {
                if (faultInjectionControl.FaultInjectionType == FaultInjectionType.ExceptionBeforeStore)
                    faultInjector.InjectBeforeStore = true;
                if (faultInjectionControl.FaultInjectionType == FaultInjectionType.ExceptionAfterStore)
                    faultInjector.InjectAfterStore = true;
                logger.LogInformation(
                    "Grain {GrainInstance} injected fault before transaction {TransactionId} Confirm, with fault type {FaultInjectionType}",
                    faultInjectionControl.FaultInjectionType,
                    context.GrainInstance,
                    transactionId);
            }
            await tResource.Confirm(transactionId, timeStamp);
            if (faultInjectionControl.FaultInjectionPhase == TransactionFaultInjectPhase.AfterConfirm
                && faultInjectionControl.FaultInjectionType == FaultInjectionType.Deactivation)
            {
                grainRuntime.DeactivateOnIdle(context);
                logger.LogInformation(
                    "Grain {GrainInstance} deactivating after transaction {TransactionId} Confirm",
                    context.GrainInstance,
                    transactionId);
            }
            faultInjectionControl.Reset();
        }

        public async Task Prepare(Guid transactionId, AccessCounter accessCount, DateTime timeStamp, ParticipantId transactionManager)
        {
            logger.LogInformation(
                "Grain {GrainInstance} started Prepare transaction {TransactionId}",
                context.GrainInstance,
                transactionId);

            if (faultInjectionControl?.FaultInjectionPhase == TransactionFaultInjectPhase.BeforePrepare)
            {
                if (faultInjectionControl.FaultInjectionType == FaultInjectionType.ExceptionBeforeStore)
                    faultInjector.InjectBeforeStore = true;
                if (faultInjectionControl.FaultInjectionType == FaultInjectionType.ExceptionAfterStore)
                    faultInjector.InjectAfterStore = true;
                logger.LogInformation(
                    "Grain {GrainInstance} injected fault before transaction {TransactionId} Prepare, with fault type {FaultInjectionType}",
                    context.GrainInstance,
                    transactionId,
                    faultInjectionControl.FaultInjectionType);
            }

            await tResource.Prepare(transactionId, accessCount, timeStamp, transactionManager);
            if (faultInjectionControl.FaultInjectionPhase == TransactionFaultInjectPhase.AfterPrepare
                && faultInjectionControl.FaultInjectionType == FaultInjectionType.Deactivation)
            {
                grainRuntime.DeactivateOnIdle(context);
                logger.LogInformation("Grain {GrainInstance} deactivating after transaction {TransactionId} Prepare", context.GrainInstance, transactionId);
            }
            faultInjectionControl.Reset();
        }
    }
}
