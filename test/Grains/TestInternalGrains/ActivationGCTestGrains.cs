using System;
using System.Threading.Tasks;
using Orleans;
using Orleans.Concurrency;
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
        private readonly string _id = Guid.NewGuid().ToString();

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
            return Task.FromResult(_id);
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
            var other = grainId.Type;
            var self = Data.Address.GrainId.Type;
            if (other == self)
            {
                IBusyActivationGcTestGrain1 g = GrainFactory.GetGrain<IBusyActivationGcTestGrain1>(grainId);
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

    // Use this Test Class in Non.Silo test [SiloBuilder_GrainCollectionOptionsForZeroSecondsAgeLimitTest]
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
        private readonly string _id = Guid.NewGuid().ToString();

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
            return Task.FromResult(_id);
        }

    }
}
