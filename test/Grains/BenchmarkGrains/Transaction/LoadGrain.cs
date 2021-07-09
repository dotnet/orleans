using System;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Diagnostics;
using Orleans;
using BenchmarkGrainInterfaces.Transaction;
using Orleans.Transactions;

namespace BenchmarkGrains.Transaction
{
    [GrainType("txload")]
    public class LoadGrain : Grain, ILoadGrain
    {
        private Task<Report> runTask;

        public Task Generate(int run, int transactions, int conncurrent)
        {
            this.runTask = RunGeneration(run, transactions, conncurrent);
            this.runTask.Ignore();
            return Task.CompletedTask;
        }

        public async Task<Report> TryGetReport()
        {
            if (!this.runTask.IsCompleted) return default(Report);
            return await this.runTask;
        }

        private async Task<Report> RunGeneration(int run, int transactions, int conncurrent)
        {
            List<Task> pending = new List<Task>();
            Report report = new Report();
            Stopwatch sw = Stopwatch.StartNew();
            int generated = run * transactions * 2;
            int max = generated + transactions;
            while (generated < max)
            {
                while (generated < max && pending.Count < conncurrent)
                {
                    pending.Add(StartTransaction(generated++));
                }
                pending = await ResolvePending(pending, report);
            }
            await ResolvePending(pending, report, true);
            sw.Stop();
            report.Elapsed = sw.Elapsed;
            return report;
        }

        private async Task<List<Task>> ResolvePending(List<Task> pending, Report report, bool all = false)
        {
            try
            {
                if(all)
                {
                    await Task.WhenAll(pending);
                }
                else
                {
                    await Task.WhenAny(pending);
                }
            } catch (Exception) {}
            List<Task> remaining = new List<Task>();
            foreach (Task t in pending)
            {
                if (t.IsFaulted || t.IsCanceled)
                {
                    if(t.Exception.Flatten().GetBaseException() is OrleansStartTransactionFailedException)
                    {
                        report.Throttled++;

                    } else
                    {
                        report.Failed++;
                    }
                }
                else if (t.IsCompleted)
                {
                    report.Succeeded++;
                }
                else
                {
                    remaining.Add(t);
                }
            }
            return remaining;
        }

        private async Task StartTransaction(int index)
        {
            try
            {
                await GrainFactory.GetGrain<ITransactionRootGrain>(Guid.Empty).Run(new List<int>() { index * 2, index * 2 + 1 });
            } catch(OrleansStartTransactionFailedException)
            {
                // Depay before retry
                await Task.Delay(TimeSpan.FromSeconds(1));
                throw;
            }
        }
    }
}
