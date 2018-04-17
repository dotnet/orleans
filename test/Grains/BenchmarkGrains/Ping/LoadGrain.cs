using System;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using Orleans;
using BenchmarkGrainInterfaces.Ping;

namespace BenchmarkGrains.Ping
{
    public class LoadGrain : Grain, ILoadGrain
    {
        private Task<Report> runTask;

        public Task Generate(int run, int conncurrent, TimeSpan duration)
        {
            this.runTask = RunGeneration(run, conncurrent, duration);
            return Task.CompletedTask;
        }

        public async Task<Report> TryGetReport()
        {
            if (!this.runTask.IsCompleted) return default(Report);
            return await this.runTask;
        }

        private async Task<Report> RunGeneration(int run, int conncurrent, TimeSpan duration)
        {
            List<Pending> pendingWork = Enumerable.Range(run * conncurrent, conncurrent).Select(i => new Pending() { Grain = GrainFactory.GetGrain<IPingGrain>(i) }).ToList();
            Report report = new Report();
            Stopwatch sw = Stopwatch.StartNew();
            while (sw.Elapsed < duration)
            {
                foreach(Pending pending in pendingWork.Where(t => t.PendingCall == null))
                {
                    pending.PendingCall = pending.Grain.Run();
                }
                await ResolvePending(pendingWork, report);
            }
            await ResolvePending(pendingWork, report, true);
            sw.Stop();
            report.Elapsed = sw.Elapsed;
            return report;
        }

        private async Task ResolvePending(List<Pending> pendingWork, Report report, bool all = false)
        {
            try
            {
                if(all)
                {
                    await Task.WhenAll(pendingWork.Select(p => p.PendingCall).Where(t => t!=null));
                }
                else
                {
                    await Task.WhenAny(pendingWork.Select(p => p.PendingCall).Where(t => t != null));
                }
            } catch (Exception) {}
            foreach (Pending pending in pendingWork.Where(p => p.PendingCall != null))
            {
                if (pending.PendingCall.IsFaulted || pending.PendingCall.IsCanceled)
                {
                    report.Failed++;
                    pending.PendingCall = null;
                }
                else if (pending.PendingCall.IsCompleted)
                {
                    report.Succeeded++;
                    pending.PendingCall = null;
                }
            }
        }
        
        private class Pending
        {
            public IPingGrain Grain { get; set; }
            public Task PendingCall { get; set; }
        }
    }
}
