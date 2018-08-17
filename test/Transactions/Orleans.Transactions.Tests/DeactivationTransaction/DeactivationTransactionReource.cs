using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;
using Orleans.Transactions.Abstractions;
using Orleans.Transactions.State;
using Orleans.Transactions.Tests.DeactivatingInjection;

namespace Orleans.Transactions.Tests.DeactivatingInjection
{
    internal class DeactivationTransactionTransactionManager<TState> : ITransactionManager
        where TState : class, new()
    {
        private TransactionManager<TState> tm;
        private readonly IGrainRuntime grainRuntime;
        private readonly IGrainActivationContext context;
        private TransactionDeactivationPhaseReference deactivationPhaseReference;
        private readonly ILogger logger;
        public DeactivationTransactionTransactionManager(TransactionDeactivationPhaseReference deactivationPhaseReference, TransactionManager<TState> tm, IGrainActivationContext activationContext, ILogger logger, IGrainRuntime grainRuntime)
        {
            this.grainRuntime = grainRuntime;
            this.tm = tm;
            this.deactivationPhaseReference = deactivationPhaseReference;
            this.logger = logger;
            this.context = activationContext;
        }

        public async Task<TransactionalStatus> PrepareAndCommit(Guid transactionId, AccessCounter accessCount, DateTime timeStamp, List<ParticipantId> writeParticipants, int totalParticipants)
        {
            this.logger.Info($"Grain {this.context.GrainInstance} started PrepareAndCommit transaction {transactionId}");
            var result = await this.tm.PrepareAndCommit(transactionId, accessCount, timeStamp, writeParticipants, totalParticipants);
            if (this.deactivationPhaseReference.DeactivationPhase == TransactionDeactivationPhase.AfterPrepareAndCommit)
            {
                this.grainRuntime.DeactivateOnIdle((context.GrainInstance));
                this.deactivationPhaseReference.DeactivationPhase = TransactionDeactivationPhase.None;
                this.logger.Info($"Grain {this.context.GrainInstance} deactivating after transaction {transactionId} PrepareAndCommit");
            }
            return result;
        }

        public async Task Prepared(Guid transactionId, DateTime timeStamp, ParticipantId participant, TransactionalStatus status)
        {
            this.logger.Info($"Grain {this.context.GrainInstance} started Prepared transaction {transactionId}");
            await this.tm.Prepared(transactionId, timeStamp, participant, status);
            if (this.deactivationPhaseReference.DeactivationPhase == TransactionDeactivationPhase.AfterPrepared)
            {
                this.grainRuntime.DeactivateOnIdle((context.GrainInstance));
                this.deactivationPhaseReference.DeactivationPhase = TransactionDeactivationPhase.None;
                this.logger.Info($"Grain {this.context.GrainInstance} deactivating after transaction {transactionId} Prepared");
            }
        }

        public async Task Ping(Guid transactionId, DateTime timeStamp, ParticipantId participant)
        {
            this.logger.Info($"Grain {this.context.GrainInstance} started Ping transaction {transactionId}");
            await this.tm.Ping(transactionId, timeStamp, participant);
            if (this.deactivationPhaseReference.DeactivationPhase == TransactionDeactivationPhase.AfterPing)
            {
                this.grainRuntime.DeactivateOnIdle((context.GrainInstance));
                this.deactivationPhaseReference.DeactivationPhase = TransactionDeactivationPhase.None;
                this.logger.Info($"Grain {this.context.GrainInstance} deactivating after transaction {transactionId} Ping");
            }
        }

    }

    internal class DeactivationTransactionalResource<TState> : ITransactionalResource
        where TState : class, new()
    {

        private readonly IGrainRuntime grainRuntime;
        private readonly IGrainActivationContext context;
        private TransactionDeactivationPhaseReference deactivationPhaseReference;
        private readonly TransactionalResource<TState> tResource;
        private readonly ILogger logger;
        public DeactivationTransactionalResource(TransactionDeactivationPhaseReference deactivationPhaseReference, TransactionalResource<TState> tResource, IGrainActivationContext activationContext, ILogger logger, IGrainRuntime grainRuntime)
        {
            this.grainRuntime = grainRuntime;
            this.tResource = tResource;
            this.deactivationPhaseReference = deactivationPhaseReference;
            this.logger = logger;
            this.context = activationContext;
        }

        public async Task<TransactionalStatus> CommitReadOnly(Guid transactionId, AccessCounter accessCount, DateTime timeStamp)
        {
            this.logger.Info($"Grain {this.context.GrainInstance} started CommitReadOnly transaction {transactionId}");
            var result = await this.tResource.CommitReadOnly(transactionId, accessCount, timeStamp);
            if (this.deactivationPhaseReference.DeactivationPhase == TransactionDeactivationPhase.AfterCommitReadOnly)
            {
                this.grainRuntime.DeactivateOnIdle((context.GrainInstance));
                this.deactivationPhaseReference.DeactivationPhase = TransactionDeactivationPhase.None;
                this.logger.Info($"Grain {this.context.GrainInstance} deactivating after transaction {transactionId} CommitReadOnly");
            }
            return result;
        }

        public async Task Abort(Guid transactionId)
        {
            this.logger.Info($"Grain {this.context.GrainInstance} aborting transaction {transactionId}");
            await this.tResource.Abort(transactionId);
            if (this.deactivationPhaseReference.DeactivationPhase == TransactionDeactivationPhase.AfterAbort)
            {
                this.grainRuntime.DeactivateOnIdle((context.GrainInstance));
                this.deactivationPhaseReference.DeactivationPhase = TransactionDeactivationPhase.None;
                this.logger.Info($"Grain {this.context.GrainInstance} deactivating after transaction {transactionId} abort");
            }
        }

        public async Task Cancel(Guid transactionId, DateTime timeStamp, TransactionalStatus status)
        {
            this.logger.Info($"Grain {this.context.GrainInstance} canceling transaction {transactionId}");
            await this.tResource.Cancel(transactionId, timeStamp, status);
            if (this.deactivationPhaseReference.DeactivationPhase == TransactionDeactivationPhase.AfterCancel)
            {
                this.grainRuntime.DeactivateOnIdle((context.GrainInstance));
                this.deactivationPhaseReference.DeactivationPhase = TransactionDeactivationPhase.None;
                this.logger.Info($"Grain {this.context.GrainInstance} deactivating after transaction {transactionId} cancel");
            }
        }

        public async Task Confirm(Guid transactionId, DateTime timeStamp)
        {
            this.logger.Info($"Grain {this.context.GrainInstance} started Confirm transaction {transactionId}");
            await this.tResource.Confirm(transactionId, timeStamp);
            if (this.deactivationPhaseReference.DeactivationPhase == TransactionDeactivationPhase.AfterConfirm)
            {
                this.grainRuntime.DeactivateOnIdle((context.GrainInstance));
                this.deactivationPhaseReference.DeactivationPhase = TransactionDeactivationPhase.None;
                this.logger.Info($"Grain {this.context.GrainInstance} deactivating after transaction {transactionId} Confirm");
            }
        }

        public async Task Prepare(Guid transactionId, AccessCounter accessCount, DateTime timeStamp, ParticipantId transactionManager)
        {
            this.logger.Info($"Grain {this.context.GrainInstance} started Prepare transaction {transactionId}");
            await this.tResource.Prepare(transactionId, accessCount, timeStamp, transactionManager);
            if (this.deactivationPhaseReference.DeactivationPhase == TransactionDeactivationPhase.AfterPrepare)
            {
                this.grainRuntime.DeactivateOnIdle((context.GrainInstance));
                this.deactivationPhaseReference.DeactivationPhase = TransactionDeactivationPhase.None;
                this.logger.Info($"Grain {this.context.GrainInstance} deactivating after transaction {transactionId} Prepare");
            }
        }
    }
}
