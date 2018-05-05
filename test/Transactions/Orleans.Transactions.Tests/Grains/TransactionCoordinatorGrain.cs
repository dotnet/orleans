﻿using Orleans.Concurrency;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Orleans.Transactions.Tests
{
    [StatelessWorker]
    public class TransactionCoordinatorGrain : Grain, ITransactionCoordinatorGrain
    {
        public Task MultiGrainSet(List<ITransactionTestGrain> grains, int newValue)
        {
            return Task.WhenAll(grains.Select(g => g.Set(newValue)));
        }

        public Task MultiGrainAdd(List<ITransactionTestGrain> grains, int numberToAdd)
        {
            return Task.WhenAll(grains.Select(g => g.Add(numberToAdd)));
        }

        public Task MultiGrainDouble(List<ITransactionTestGrain> grains)
        {
            return Task.WhenAll(grains.Select(Double));
        }

        public Task OrphanCallTransaction(ITransactionTestGrain grain)
        {
            Task t = grain.Add(1000);
            return Task.CompletedTask;
        }

        public async Task AddAndThrow(ITransactionTestGrain grain, int numberToAdd)
        {
            await grain.Add(numberToAdd);
            throw new Exception("This should abort the transaction");
        }

        public async Task MultiGrainAddAndThrow(ITransactionTestGrain throwGrain, List<ITransactionTestGrain> grains, int numberToAdd)
        {
            await Task.WhenAll(grains.Select(g => g.Add(numberToAdd)));
            await throwGrain.AddAndThrow(numberToAdd);
        }

        private async Task Double(ITransactionTestGrain grain)
        {
            int[] values = await grain.Get();
            await grain.Add(values[0]);
        }
    }
}
