﻿using Orleans;
using BenchmarkGrainInterfaces.Ping;
using System.Threading.Tasks;

namespace BenchmarkGrains.Ping
{
    public class PingGrain : Grain, IPingGrain
    {
        private IPingGrain self;

        public override Task OnActivateAsync()
        {
            this.self = this.AsReference<IPingGrain>();
            return base.OnActivateAsync();
        }

        public Task Run()
        {
            return Task.CompletedTask;
        }

        public Task PingPongInterleave(IPingGrain other, int count)
        {
            if (count == 0) return Task.CompletedTask;
            return other.PingPongInterleave(this.self, count - 1);
        }
    }
}
