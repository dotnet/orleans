using Orleans.Concurrency;
using Orleans.Runtime;
using UnitTests.GrainInterfaces;

namespace UnitTests.Grains
{
    public class IdleActivationGcTestGrain1: Grain, IIdleActivationGcTestGrain1
    {
        public Task Nop() => Task.CompletedTask;
    }

    public class IdleActivationGcTestGrain2: Grain, IIdleActivationGcTestGrain2
    {
        public Task Nop() => Task.CompletedTask;
    }

    internal class BusyActivationGcTestGrain1: Grain, IBusyActivationGcTestGrain1
    {
        private readonly string _id = Guid.NewGuid().ToString();
        private readonly ActivationCollector activationCollector;
        private readonly IGrainContext _grainContext;

        private int burstCount = 0;

        public BusyActivationGcTestGrain1(ActivationCollector activationCollector, IGrainContext grainContext)
        {
            this.activationCollector = activationCollector;
            _grainContext = grainContext;
        }

        public Task Nop() => Task.CompletedTask;

        public Task Delay(TimeSpan dt) => Task.Delay(dt);

        public Task<string> IdentifyActivation() => Task.FromResult(_id);

        public Task EnableBurstOnCollection(int count)
        {
            if (0 == count)
            {
                throw new ArgumentOutOfRangeException("count");
            }

            burstCount = count;
            activationCollector.Debug_OnDecideToCollectActivation = OnCollectActivation;
            return Task.CompletedTask;
        }

        private void OnCollectActivation(GrainId grainId)
        {
            var other = grainId.Type;
            var self = _grainContext.Address.GrainId.Type;
            if (other == self)
            {
                var g = GrainFactory.GetGrain<IBusyActivationGcTestGrain1>(grainId);
                for (var i = 0; i < burstCount; ++i)
                {
                    g.Delay(TimeSpan.FromMilliseconds(10)).Ignore();
                }
            }
        }
    }

    public class BusyActivationGcTestGrain2: Grain, IBusyActivationGcTestGrain2
    {
        public Task Nop() => Task.CompletedTask;
    }

    public class CollectionSpecificAgeLimitForTenSecondsActivationGcTestGrain : Grain, ICollectionSpecificAgeLimitForTenSecondsActivationGcTestGrain
    {
        public Task Nop() => Task.CompletedTask;
    }

    // Use this Test Class in Non.Silo test [SiloBuilder_GrainCollectionOptionsForZeroSecondsAgeLimitTest]
    public class CollectionSpecificAgeLimitForZeroSecondsActivationGcTestGrain : Grain, ICollectionSpecificAgeLimitForZeroSecondsActivationGcTestGrain
    {
        public Task Nop() => Task.CompletedTask;
    }

    [StatelessWorker]
    public class StatelessWorkerActivationCollectorTestGrain1 : Grain, IStatelessWorkerActivationCollectorTestGrain1
    {
        private readonly string _id = Guid.NewGuid().ToString();

        public Task Nop() => Task.CompletedTask;

        public Task Delay(TimeSpan dt) => Task.Delay(dt);

        public Task<string> IdentifyActivation() => Task.FromResult(_id);

    }
}
