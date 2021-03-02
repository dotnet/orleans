using System;
using System.Threading.Tasks;
using System.Diagnostics;
using Orleans;
using BenchmarkGrainInterfaces.GrainStorage;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;

namespace BenchmarkGrains.GrainStorage
{
    public class PersistentGrain : Grain, IPersistentGrain
    {
        private readonly ILogger<PersistentGrain> logger;
        private readonly IPersistentState<int> persistentState;

        public PersistentGrain(
            ILogger<PersistentGrain> logger,
            [PersistentState("state")]
            IPersistentState<int> persistentState)
        {
            this.logger = logger;
            this.persistentState = persistentState;
        }

        public async Task<Report> TrySet(int value)
        {
            Stopwatch sw = Stopwatch.StartNew();
            bool success;
            try
            {
                this.persistentState.State = value;
                await this.persistentState.WriteStateAsync();
                sw.Stop();
                object[] args = { this.GetPrimaryKey(), sw.ElapsedMilliseconds };
                logger.LogInformation("Grain {GrainId} took {WriteTimeMs}ms to set state.", args);
                success = true;
            } catch(Exception ex)
            {
                sw.Stop();
                object[] args = { this.GetPrimaryKey(), sw.ElapsedMilliseconds };
                this.logger.LogError(ex, "Grain {GrainId} failed to set state in {WriteTimeMs}ms to set state.", args);
                success = false;
            }

            return new Report
            {
                Success = success,
                Elapsed = sw.Elapsed,
                State = this.persistentState.State,
            };
        }
    }
}
