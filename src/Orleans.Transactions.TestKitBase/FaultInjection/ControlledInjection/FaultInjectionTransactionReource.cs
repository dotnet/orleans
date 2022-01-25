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
            this.logger.Info($"Grain {this.context.GrainInstance} started PrepareAndCommit transaction {transactionId}");
            if (this.faultInjectionControl.FaultInjectionPhase == TransactionFaultInjectPhase.BeforePrepareAndCommit)
            {
                if (this.faultInjectionControl.FaultInjectionType == FaultInjectionType.ExceptionBeforeStore)
                    this.faultInjector.InjectBeforeStore = true;
                if (this.faultInjectionControl.FaultInjectionType == FaultInjectionType.ExceptionAfterStore)
                    this.faultInjector.InjectAfterStore = true;
                this.logger.Info($"Grain {this.context.GrainInstance} injected fault before transaction {transactionId} PrepareAndCommit, " +
                                 $"with fault type {this.faultInjectionControl.FaultInjectionType}");
            }
            var result = await this.tm.PrepareAndCommit(transactionId, accessCount, timeStamp, writeParticipants, totalParticipants);
            if (this.faultInjectionControl?.FaultInjectionPhase == TransactionFaultInjectPhase.AfterPrepareAndCommit && this.faultInjectionControl.FaultInjectionType == FaultInjectionType.Deactivation)
            {
                this.grainRuntime.DeactivateOnIdle(context);
                this.logger.Info($"Grain {this.context.GrainInstance} deactivating after transaction {transactionId} PrepareAndCommit");
            }
            this.faultInjectionControl.Reset();
            return result;
        }

        public async Task Prepared(Guid transactionId, DateTime timeStamp, ParticipantId participant, TransactionalStatus status)
        {
            this.logger.Info($"Grain {this.context.GrainInstance} started Prepared transaction {transactionId}");
            await this.tm.Prepared(transactionId, timeStamp, participant, status);
            if (this.faultInjectionControl.FaultInjectionPhase == TransactionFaultInjectPhase.AfterPrepared
                && this.faultInjectionControl?.FaultInjectionType == FaultInjectionType.Deactivation)
            {
                this.grainRuntime.DeactivateOnIdle(context);
                this.logger.Info($"Grain {this.context.GrainInstance} deactivating after transaction {transactionId} Prepared");
            }
            this.faultInjectionControl.Reset();
        }

        public async Task Ping(Guid transactionId, DateTime timeStamp, ParticipantId participant)
        {
            this.logger.Info($"Grain {this.context.GrainInstance} started Ping transaction {transactionId}");
            await this.tm.Ping(transactionId, timeStamp, participant);
            if (this.faultInjectionControl?.FaultInjectionPhase == TransactionFaultInjectPhase.AfterPing
                && this.faultInjectionControl.FaultInjectionType == FaultInjectionType.Deactivation)
            {
                this.grainRuntime.DeactivateOnIdle(context);
                this.logger.Info($"Grain {this.context.GrainInstance} deactivating after transaction {transactionId} Ping");
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
            this.logger.Info($"Grain {this.context.GrainInstance} started CommitReadOnly transaction {transactionId}");
            var result = await this.tResource.CommitReadOnly(transactionId, accessCount, timeStamp);
            if (this.faultInjectionControl.FaultInjectionPhase == TransactionFaultInjectPhase.AfterCommitReadOnly
                && this.faultInjectionControl.FaultInjectionType == FaultInjectionType.Deactivation)
            {
                this.grainRuntime.DeactivateOnIdle(context);
                this.logger.Info($"Grain {this.context.GrainInstance} deactivating after transaction {transactionId} CommitReadOnly");
            }

            this.faultInjectionControl.Reset();
            return result;
        }

        public async Task Abort(Guid transactionId)
        {
            this.logger.Info($"Grain {this.context.GrainInstance} aborting transaction {transactionId}");
            await this.tResource.Abort(transactionId);
            if (this.faultInjectionControl.FaultInjectionPhase == TransactionFaultInjectPhase.AfterAbort
                && this.faultInjectionControl.FaultInjectionType == FaultInjectionType.Deactivation)
            {
                this.grainRuntime.DeactivateOnIdle(context);
                this.logger.Info($"Grain {this.context.GrainInstance} deactivating after transaction {transactionId} abort");
            }
            this.faultInjectionControl.Reset();
        }

        public async Task Cancel(Guid transactionId, DateTime timeStamp, TransactionalStatus status)
        {
            this.logger.Info($"Grain {this.context.GrainInstance} canceling transaction {transactionId}");
            await this.tResource.Cancel(transactionId, timeStamp, status);
            if (this.faultInjectionControl.FaultInjectionPhase == TransactionFaultInjectPhase.AfterCancel
                && this.faultInjectionControl.FaultInjectionType == FaultInjectionType.Deactivation)
            {
                this.grainRuntime.DeactivateOnIdle(context);
                this.logger.Info($"Grain {this.context.GrainInstance} deactivating after transaction {transactionId} cancel");
            }
            this.faultInjectionControl.Reset();
        }

        public async Task Confirm(Guid transactionId, DateTime timeStamp)
        {
            this.logger.Info($"Grain {this.context.GrainInstance} started Confirm transaction {transactionId}");
            if (this.faultInjectionControl?.FaultInjectionPhase == TransactionFaultInjectPhase.BeforeConfirm)
            {
                if (this.faultInjectionControl.FaultInjectionType == FaultInjectionType.ExceptionBeforeStore)
                    this.faultInjector.InjectBeforeStore = true;
                if (this.faultInjectionControl.FaultInjectionType == FaultInjectionType.ExceptionAfterStore)
                    this.faultInjector.InjectAfterStore = true;
                this.logger.Info($"Grain {this.context.GrainInstance} injected fault before transaction {transactionId} Confirm, " +
                                 $"with fault type {this.faultInjectionControl.FaultInjectionType}");
            }
            await this.tResource.Confirm(transactionId, timeStamp);
            if (this.faultInjectionControl.FaultInjectionPhase == TransactionFaultInjectPhase.AfterConfirm
                && this.faultInjectionControl.FaultInjectionType == FaultInjectionType.Deactivation)
            {
                this.grainRuntime.DeactivateOnIdle(context);
                this.logger.Info($"Grain {this.context.GrainInstance} deactivating after transaction {transactionId} Confirm");
            }
            this.faultInjectionControl.Reset();
        }

        public async Task Prepare(Guid transactionId, AccessCounter accessCount, DateTime timeStamp, ParticipantId transactionManager)
        {
            this.logger.Info($"Grain {this.context.GrainInstance} started Prepare transaction {transactionId}");

            if (this.faultInjectionControl?.FaultInjectionPhase == TransactionFaultInjectPhase.BeforePrepare)
            {
                if (this.faultInjectionControl.FaultInjectionType == FaultInjectionType.ExceptionBeforeStore)
                    this.faultInjector.InjectBeforeStore = true;
                if (this.faultInjectionControl.FaultInjectionType == FaultInjectionType.ExceptionAfterStore)
                    this.faultInjector.InjectAfterStore = true;
                this.logger.Info($"Grain {this.context.GrainInstance} injected fault before transaction {transactionId} Prepare, " +
                                 $"with fault type {this.faultInjectionControl.FaultInjectionType}");
            }

            await this.tResource.Prepare(transactionId, accessCount, timeStamp, transactionManager);
            if (this.faultInjectionControl.FaultInjectionPhase == TransactionFaultInjectPhase.AfterPrepare
                && this.faultInjectionControl.FaultInjectionType == FaultInjectionType.Deactivation)
            {
                this.grainRuntime.DeactivateOnIdle(context);
                this.logger.Info($"Grain {this.context.GrainInstance} deactivating after transaction {transactionId} Prepare");
            }
            this.faultInjectionControl.Reset();
        }
    }
}
