using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;
using Orleans.Providers;
using Orleans.Transactions.Abstractions;
using Orleans.Transactions;

namespace Orleans.Transactions.Tests
{
    public class TransactionOrchestrationGrain : Grain, ITransactionTestGrain
    {
        private readonly TransactionalResource resource = new TransactionalResource();

        public override Task OnActivateAsync()
        {
            var resultGrain = this.GrainFactory.GetGrain<ITransactionOrchestrationResultGrain>(this.GetPrimaryKey());
            return this.resource.BindAsync(this, this.ServiceProvider, resultGrain);
        }

        public Task Set(int newValue)
        {
            this.resource.JoinTransaction();
            return Task.CompletedTask;
        }

        public Task<int> Add(int numberToAdd)
        {
            this.resource.JoinTransaction();
            return Task.FromResult<int>(0);
        }

        public Task<int> Get()
        {
            this.resource.JoinTransaction();
            return Task.FromResult<int>(0);
        }

        public Task<int> AddAndThrow(int numberToAdd)
        {
            throw new NotImplementedException(nameof(AddAndThrow));
        }

        public Task Deactivate()
        {
            DeactivateOnIdle();
            return Task.CompletedTask;
        }

        private class TransactionalResource : ITransactionalResource
        {
            private ITransactionOrchestrationResultGrain resultGrain;
            private Grain grain;

            private ILogger logger;
            private ITransactionalResource transactionalResource;

            private readonly List<long> transactions= new List<long>();

            private TransactionalResourceVersion version;
            private long stableVersion;

            private long writeLowerBound;

            public async Task<bool> Prepare(long transactionId, TransactionalResourceVersion? writeVersion,
                TransactionalResourceVersion? readVersion)
            {
                await this.resultGrain.RecordPrepare(transactionId);
                return true;
            }

            public Task Abort(long transactionId)
            {
                logger.Info($"Transaction {transactionId} was aborted for grain {grain}.");
                return this.resultGrain.RecordAbort(transactionId);
            }

            public Task Commit(long transactionId)
            {
                logger.Info($"Transaction {transactionId} was committed for grain {grain}.");
                this.stableVersion = transactionId;
                return this.resultGrain.RecordCommit(transactionId);
            }

            public void JoinTransaction()
            {
                TransactionInfo info = TransactionContext.GetRequiredTransactionInfo<TransactionInfo>();
                logger.Info($"Grain {grain} is joining transaction {info.TransactionId}.");

                // are we already part of the transaction?
                if (this.transactions.Contains(info.TransactionId))
                {
                    return;
                }

                TransactionalResourceVersion readVersion;
                if (!TryGetVersion(info.TransactionId, out readVersion))
                {
                    throw new OrleansTransactionVersionDeletedException(info.TransactionId.ToString());
                }

                if (info.IsReadOnly && readVersion.TransactionId > this.stableVersion)
                {
                    throw new OrleansTransactionUnstableVersionException(info.TransactionId.ToString());
                }

                info.RecordRead(transactionalResource, readVersion, this.stableVersion);

                writeLowerBound = Math.Max(writeLowerBound, info.TransactionId - 1);

                if (this.version.TransactionId > info.TransactionId || this.writeLowerBound >= info.TransactionId)
                {
                    throw new OrleansTransactionWaitDieException(info.TransactionId.ToString());
                }

                TransactionalResourceVersion nextVersion = TransactionalResourceVersion.Create(info.TransactionId,
                    this.version.TransactionId == info.TransactionId ? this.version.WriteNumber + 1 : 1);

                info.RecordWrite(transactionalResource, this.version, this.stableVersion);

                this.version = nextVersion;

                this.transactions.Remove(info.TransactionId);
            }

            public async Task BindAsync(Grain containerGrain, IServiceProvider services, ITransactionOrchestrationResultGrain resultGrain)
            {
                this.grain = containerGrain;
                this.resultGrain = resultGrain;
                this.logger = services.GetService<ILogger<TransactionalResource>>();

                // bind extension to grain
                IProviderRuntime runtime = services.GetRequiredService<IProviderRuntime>();
                Tuple<TransactionalExtension, ITransactionalExtension> boundExtension = await runtime.BindExtension<TransactionalExtension, ITransactionalExtension>(() => new TransactionalExtension());
                boundExtension.Item1.Register(nameof(TransactionalResource), this);
                this.transactionalResource = boundExtension.Item2.AsTransactionalResource(nameof(TransactionalResource));
            }

            public bool Equals(ITransactionalResource other)
            {
                return transactionalResource.Equals(other);
            }

            private bool TryGetVersion(long transactionId, out TransactionalResourceVersion readVersion)
            {
                readVersion = this.version;
                return this.version.TransactionId <= transactionId;
            }
        }
    }
}
