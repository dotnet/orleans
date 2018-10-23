using System;
using System.Threading.Tasks;
using System.Diagnostics;
using Orleans;
using BenchmarkGrainInterfaces.GrainStorage;
using Microsoft.Extensions.Logging;

namespace BenchmarkGrains.GrainStorage
{
    public class PersistentGrain : Grain<int>, IPersistentGrain
    {
        private readonly ILogger<PersistentGrain> logger;

        public PersistentGrain(ILogger<PersistentGrain> logger)
        {
            this.logger = logger;
        }

        public async Task<int> Set(int value)
        {
            Stopwatch sw = Stopwatch.StartNew();
            try
            {
                this.State = value;
                await this.WriteStateAsync();
                sw.Stop();
                object[] args = { this.GetPrimaryKey(), sw.ElapsedMilliseconds };
                logger.LogInformation("Grain {GrainId} took {WriteTimeMs}ms to set state.", args);
            } catch(Exception ex)
            {
                sw.Stop();
                object[] args = { this.GetPrimaryKey(), sw.ElapsedMilliseconds };
                this.logger.LogError(ex, "Grain {GrainId} failed to set state in {WriteTimeMs}ms to set state.", args);
                throw;
            }

            return this.State;
        }
    }
}
