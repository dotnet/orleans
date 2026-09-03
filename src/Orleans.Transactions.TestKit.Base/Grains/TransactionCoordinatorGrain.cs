using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Orleans.Concurrency;
using Orleans.Transactions;
using Orleans.Transactions.Abstractions;
using Orleans.Transactions.TestKit.Correctnesss;

namespace Orleans.Transactions.TestKit
{
    /// <summary>
    /// Coordinates transactions across transaction test grains.
    /// </summary>
    [StatelessWorker]
    public class TransactionCoordinatorGrain : Grain, ITransactionCoordinatorGrain
    {
        /// <inheritdoc/>
        public Task MultiGrainSet(List<ITransactionTestGrain> grains, int newValue)
        {
            return Task.WhenAll(grains.Select(g => g.Set(newValue)));
        }

        /// <inheritdoc/>
        public Task MultiGrainAdd(List<ITransactionTestGrain> grains, int numberToAdd)
        {
            return Task.WhenAll(grains.Select(g => g.Add(numberToAdd)));
        }

        /// <inheritdoc/>
        public Task MultiGrainDouble(List<ITransactionTestGrain> grains)
        {
            return Task.WhenAll(grains.Select(Double));
        }

        /// <inheritdoc/>
        public Task OrphanCallTransaction()
        {
            _ = TransactionContext.GetRequiredTransactionInfo().Fork();
            return Task.CompletedTask;
        }

        /// <inheritdoc/>
        public async Task AddAndThrow(ITransactionTestGrain grain, int numberToAdd)
        {
            await grain.Add(numberToAdd);
            throw new Exception("This should abort the transaction");
        }

        /// <inheritdoc/>
        public async Task MultiGrainAddAndThrow(List<ITransactionTestGrain> throwGrains, List<ITransactionTestGrain> grains, int numberToAdd)
        {
            await Task.WhenAll(grains.Select(g => g.Add(numberToAdd)));
            await Task.WhenAll(throwGrains.Select(tg => tg.AddAndThrow(numberToAdd)));
        }

        /// <inheritdoc/>
        public Task MultiGrainSetBit(List<ITransactionalBitArrayGrain> grains, int bitIndex)
        {
            return Task.WhenAll(grains.Select(g => g.SetBit(bitIndex)));
        }

        /// <inheritdoc/>
        public Task MultiGrainAdd(ITransactionCommitterTestGrain committer, ITransactionCommitOperation<IRemoteCommitService> operation, List<ITransactionTestGrain> grains, int numberToAdd)
        {
            List<Task> tasks = new List<Task>();
            tasks.AddRange(grains.Select(g => g.Add(numberToAdd)));
            tasks.Add(committer.Commit(operation));
            return Task.WhenAll(tasks);
        }

        /// <inheritdoc/>
        public Task UpdateViolated(ITransactionTestGrain grain, int numberToAdd)
        {
            return grain.Add(numberToAdd);
        }

        private async Task Double(ITransactionTestGrain grain)
        {
            int[] values = await grain.Get();
            await grain.Add(values[0]);
        }

        /// <inheritdoc/>
        public async Task MultiGrainDoubleByRWRW(List<ITransactionTestGrain> grains, int numberToAdd)
        {
            await Task.WhenAll(grains.Select(g => g.Get()));
            await Task.WhenAll(grains.Select(g => g.Add(numberToAdd)));
            await Task.WhenAll(grains.Select(g => g.Get()));
            await Task.WhenAll(grains.Select(g => g.Add(numberToAdd)));
        }

        /// <inheritdoc/>
        public async Task MultiGrainDoubleByWRWR(List<ITransactionTestGrain> grains, int numberToAdd)
        {
            await Task.WhenAll(grains.Select(g => g.Add(numberToAdd)));
            await Task.WhenAll(grains.Select(g => g.Get()));
            await Task.WhenAll(grains.Select(g => g.Add(numberToAdd)));
            await Task.WhenAll(grains.Select(g => g.Get()));
        }
    }
}
