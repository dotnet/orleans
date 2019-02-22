﻿using System;
using System.Threading.Tasks;
using Orleans;
using Orleans.Concurrency;
using Orleans.Configuration;
using Orleans.Runtime;
using UnitTests.GrainInterfaces;

namespace UnitTests.Grains
{
    public class IdleActivationGcTestGrain1: Grain, IIdleActivationGcTestGrain1
    {
        public Task Nop()
        {
            return Task.CompletedTask;
        }
    }

    public class IdleActivationGcTestGrain2: Grain, IIdleActivationGcTestGrain2
    {
        public Task Nop()
        {
            return Task.CompletedTask;
        }
    }

    internal class BusyActivationGcTestGrain1: Grain, IBusyActivationGcTestGrain1
    {
        private readonly ActivationCollector activationCollector;
        
        private int burstCount = 0;

        public BusyActivationGcTestGrain1(ActivationCollector activationCollector)
        {
            this.activationCollector = activationCollector;
        }

        public Task Nop()
        {
            return Task.CompletedTask;
        }

        public Task Delay(TimeSpan dt)
        {
            return Task.Delay(dt);
        }

        public Task<string> IdentifyActivation()
        {
            return Task.FromResult(Data.ActivationId.ToString());
        }

        public Task EnableBurstOnCollection(int count)
        {
            if (0 == count)
            {
                throw new ArgumentOutOfRangeException("count");
            }

            burstCount = count;
            this.activationCollector.Debug_OnDecideToCollectActivation = OnCollectActivation;
            return Task.CompletedTask;
        }

        private void OnCollectActivation(GrainId grainId)
        {
            int other = grainId.TypeCode;
            int self = Data.Address.Grain.TypeCode;
            if (other == self)
            {
                IBusyActivationGcTestGrain1 g = GrainFactory.GetGrain<IBusyActivationGcTestGrain1>(grainId.GetPrimaryKey());
                for (int i = 0; i < burstCount; ++i)
                {
                    g.Delay(TimeSpan.FromMilliseconds(10)).Ignore();
                }
            }         
        }
    }

    public class BusyActivationGcTestGrain2: Grain, IBusyActivationGcTestGrain2
    {
        public Task Nop()
        {
            return Task.CompletedTask;
        }
    }

    public class CollectionSpecificAgeLimitForTenSecondsActivationGcTestGrain : Grain, ICollectionSpecificAgeLimitForTenSecondsActivationGcTestGrain
    {
        public Task Nop()
        {
            return Task.CompletedTask;
        }
    }

    // Use this Test Class in Non.Silo test [SiloHostBuilder_GrainCollectionOptionsForZeroSecondsAgeLimitTest]
    public class CollectionSpecificAgeLimitForZeroSecondsActivationGcTestGrain : Grain, ICollectionSpecificAgeLimitForZeroSecondsActivationGcTestGrain
    {
        public Task Nop()
        {
            return Task.CompletedTask;
        }
    }

    [StatelessWorker]
    public class StatelessWorkerActivationCollectorTestGrain1 : Grain, IStatelessWorkerActivationCollectorTestGrain1
    {
        public Task Nop()
        {
            return Task.CompletedTask;
        }

        public Task Delay(TimeSpan dt)
        {
            return Task.Delay(dt);
        }

        public Task<string> IdentifyActivation()
        {
            return Task.FromResult(Data.ActivationId.ToString());
        }

    }
}
