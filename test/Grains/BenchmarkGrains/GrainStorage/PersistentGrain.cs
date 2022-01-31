using System;
using System.Threading.Tasks;
using System.Diagnostics;
using Orleans;
using BenchmarkGrainInterfaces.GrainStorage;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;
using System.Linq;

namespace BenchmarkGrains.GrainStorage
{
    [Serializable]
    public class PersistentGrainState
    {
        public byte[] Payload { get; set; }
    }

    public class PersistentGrain : Grain, IPersistentGrain
    {
        private readonly ILogger<PersistentGrain> logger;
        private readonly IPersistentState<PersistentGrainState> persistentState;

        public PersistentGrain(
            ILogger<PersistentGrain> logger,
            [PersistentState("state")]
            IPersistentState<PersistentGrainState> persistentState)
        {
            this.logger = logger;
            this.persistentState = persistentState;
        }

        public async Task Init(int payloadSize)
        {
            this.persistentState.State.Payload = Enumerable.Range(0, payloadSize).Select(i => (byte)i).ToArray();
            await this.persistentState.WriteStateAsync();
        }

        public async Task<Report> TrySet(int index)
        {
            Stopwatch sw = Stopwatch.StartNew();
            bool success;
            try
            {
                await this.persistentState.ReadStateAsync();
                this.persistentState.State.Payload[index] = (byte)(this.persistentState.State.Payload[index] + 1);
                await this.persistentState.WriteStateAsync();
                sw.Stop();
                logger.LogInformation("Grain {GrainId} took {WriteTimeMs}ms to set state.", this.GetPrimaryKey(), sw.ElapsedMilliseconds);
                success = true;
            } catch(Exception ex)
            {
                sw.Stop();
                this.logger.LogError(ex, "Grain {GrainId} failed to set state in {WriteTimeMs}ms to set state.",  this.GetPrimaryKey(), sw.ElapsedMilliseconds );
                success = false;
            }

            return new Report
            {
                Success = success,
                Elapsed = sw.Elapsed,
            };
        }
    }
}
