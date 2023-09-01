using System.Diagnostics;
using BenchmarkGrainInterfaces.Ping;

namespace BenchmarkGrains.Ping
{
    public class LoadGrain : Grain, ILoadGrain
    {
        private Task<Report> runTask;
        private bool end = false;

        public Task Generate(int run, int conncurrent)
        {
            this.runTask = RunGeneration(run, conncurrent);
            return Task.CompletedTask;
        }

        public async Task<Report> TryGetReport()
        {
            this.end = true;
            return await this.runTask;
        }

        private async Task<Report> RunGeneration(int run, int conncurrent)
        {
            List<Pending> pendingWork = Enumerable.Range(run * conncurrent, conncurrent).Select(i => new Pending() { Grain = GrainFactory.GetGrain<IPingGrain>(i) }).ToList();
            Report report = new Report();
            Stopwatch sw = Stopwatch.StartNew();
            while (!this.end)
            {
                foreach(Pending pending in pendingWork.Where(t => t.PendingCall == default))
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
                    await Task.WhenAll(pendingWork.Where(t => !t.PendingCall.IsCompletedSuccessfully).Select(p => p.PendingCall.AsTask()));
                }
                else
                {
                    await Task.WhenAny(pendingWork.Where(t => !t.PendingCall.IsCompletedSuccessfully).Select(p => p.PendingCall.AsTask()));
                }
            } catch (Exception) {}
            foreach (Pending pending in pendingWork.Where(p => p.PendingCall != default))
            {
                if (pending.PendingCall.IsFaulted || pending.PendingCall.IsCanceled)
                {
                    report.Failed++;
                    pending.PendingCall = default;
                }
                else if (pending.PendingCall.IsCompleted)
                {
                    report.Succeeded++;
                    pending.PendingCall = default;
                }
            }
        }
        
        private class Pending
        {
            public IPingGrain Grain { get; set; }
            public ValueTask PendingCall { get; set; }
        }
    }
}
