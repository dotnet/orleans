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
            return TaskDone.Done;
        }
    }

    public class IdleActivationGcTestGrain2: Grain, IIdleActivationGcTestGrain2
    {
        public Task Nop()
        {
            return TaskDone.Done;
        }
    }

    public class BusyActivationGcTestGrain1: Grain, IBusyActivationGcTestGrain1
    {
        private int burstCount = 0;

        public Task Nop()
        {
            return TaskDone.Done;
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
            Silo.CurrentSilo.TestHook.Debug_OnDecideToCollectActivation = OnCollectActivation;
            return TaskDone.Done;
        }

        private void OnCollectActivation(GrainId grainId)
        {
            int other = grainId.GetTypeCode();
            int self = Data.Address.Grain.GetTypeCode();
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
            return TaskDone.Done;
        }
    }

    [StatelessWorker]
    public class StatelessWorkerActivationCollectorTestGrain1 : Grain, IStatelessWorkerActivationCollectorTestGrain1
    {
        public Task Nop()
        {
            return TaskDone.Done;
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
