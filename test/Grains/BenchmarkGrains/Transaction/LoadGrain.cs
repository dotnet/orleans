using System;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using Orleans;
using BenchmarkGrainInterfaces.Transaction;

namespace BenchmarkGrains.Transaction
{
    public class LoadGrain : Grain, ILoadGrain
    {
        private Task<Report> runTask;

        public Task Generate(int run, int transactions, int conncurrent)
        {
            this.runTask = RunGeneration(run, transactions, conncurrent);
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
                    report.Failed++;
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

        private Task StartTransaction(int index)
        {
            return GrainFactory.GetGrain<ITransactionRootGrain>(Guid.Empty).Run(new List<int>() { index * 2, index * 2 + 1 });
        }
    }
}
