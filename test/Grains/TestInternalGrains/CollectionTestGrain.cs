using System;
using System.Threading.Tasks;
using Orleans;
using Orleans.Concurrency;
using Orleans.Runtime;
using UnitTests.GrainInterfaces;

namespace UnitTests.Grains
{
    using System.Collections.Generic;

    using Orleans.CodeGeneration;

    public class CollectionTestGrain : Grain, ICollectionTestGrain
    {
        protected DateTime activated;

        private ICollectionTestGrain other;
        private Logger logger;
        private int counter;
        private static int staticCounter;

        protected virtual Logger Logger()
        {
            return logger;
        }

        public override Task OnActivateAsync()
        {
            logger = this.GetLogger(String.Format("CollectionTestGrain {0} {1} on {2}.", Identity, Data.ActivationId, RuntimeIdentity));
            logger.Info("OnActivateAsync.");
            activated = DateTime.UtcNow;
            counter = 0;
            return Task.CompletedTask;
        }

        public override Task OnDeactivateAsync()
        {
            Logger().Info("OnDeactivateAsync.");
            return Task.CompletedTask;
        }

        public virtual Task<int> IncrCounter()
        {
            staticCounter++;
            counter++;
            int tmpCounter = counter;
            Logger().Info("IncrCounter {0}, staticCounter {1}.", tmpCounter, staticCounter);
            return Task.FromResult(counter);
        }

        public Task<TimeSpan> GetAge()
        {
            Logger().Info("GetAge.");
            return Task.FromResult(DateTime.UtcNow.Subtract(activated));
        }

        public Task<DateTime> GetActivationTime()
        {
            Logger().Info("GetActivationTime.");
            return Task.FromResult(activated);
        }

        public virtual Task DeactivateSelf()
        {
            Logger().Info("DeactivateSelf.");
            DeactivateOnIdle();
            return Task.CompletedTask;
        }

        public Task SetOther(ICollectionTestGrain other)
        {
            Logger().Info("SetOther.");
            this.other = other;
            return Task.CompletedTask;
        }

        public Task<TimeSpan> GetOtherAge()
        {
            Logger().Info("GetOtherAge.");
            return other.GetAge();
        }

        public Task<string> GetRuntimeInstanceId()
        {
            Logger().Info("GetRuntimeInstanceId.");
            return Task.FromResult(RuntimeIdentity);
        }

        public Task<ICollectionTestGrain> GetGrainReference()
        {
            Logger().Info("GetGrainReference.");
            return Task.FromResult(this.AsReference<ICollectionTestGrain>());
        }

        public virtual Task StartTimer(TimeSpan timerPeriod, TimeSpan delayPeriod)
        {
            RegisterTimer(TimerCallback, delayPeriod, TimeSpan.Zero, timerPeriod);
            return Task.CompletedTask;
        }

        private async Task TimerCallback(object state)
        {
            TimeSpan delayPeriod = (TimeSpan)state;
            staticCounter++;
            counter++;
            int tmpCounter = counter;
            Logger().Info("Start TimerCallback {0}, staticCounter {1}.", tmpCounter, staticCounter);
            await Task.Delay(delayPeriod);
            Logger().Info("After first delay TimerCallback {0}, staticCounter {1}.", tmpCounter, staticCounter);
            await Task.Delay(delayPeriod);
            Logger().Info("After second delay TimerCallback {0}, staticCounter {1}.", tmpCounter, staticCounter);
        }
    }

    [Reentrant]
    public class ReentrantCollectionTestGrain : CollectionTestGrain, ICollectionTestGrain
    {
        private Logger logger;
        private int counter;
        private static int staticCounter;

        protected override Logger Logger()
        {
            return logger;
        }

        public override Task OnActivateAsync()
        {            
            logger = this.GetLogger(String.Format("ReentrantCollectionTestGrain {0} {1} on {2}.", Identity, Data.ActivationId, RuntimeIdentity));
            logger.Info("OnActivateAsync.");
            counter = 0;
            activated = DateTime.UtcNow;
            return Task.CompletedTask;
        }

        public override Task OnDeactivateAsync()
        {
            Logger().Info("OnDeactivateAsync.");
            return Task.CompletedTask;
        }

        public override async Task<int> IncrCounter()
        {
            staticCounter++;
            int tmpCounter = counter++;
            Logger().Info("Reentrant:IncrCounter BEFORE Delay {0}, staticCounter {1}.", tmpCounter, staticCounter);
            await Task.Delay(TimeSpan.FromMilliseconds(1000));
            Logger().Info("Reentrant:IncrCounter AFTER Delay {0}, staticCounter {1}.", tmpCounter, staticCounter);
            return counter;
        }
    }
	
    [MayInterleave("MayInterleave")]
    public class MayInterleaveCollectionTestGrainWithTcs : CollectionTestGrain, ICollectionTestGrain
    {
        public static bool MayInterleave(InvokeMethodRequest req) => req.MethodId == 1121447038; // IncrCounter

        private Logger logger;
        private int counter;
        
        readonly List<TaskCompletionSource<int>> buffer = new List<TaskCompletionSource<int>>();

        protected override Logger Logger()
        {
            return logger;
        }

        public override Task OnActivateAsync()
        {
            logger = this.GetLogger($"MayInterleaveCollectionTestGrainWithTcs {Identity} {Data.ActivationId} on {RuntimeIdentity}.");
            logger.Info("OnActivateAsync.");
            
            counter = 0;
            activated = DateTime.UtcNow;            
            RegisterTimer(CompleteBuffer, null, TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(100));

            return Task.CompletedTask;
        }

        public override Task OnDeactivateAsync()
        {            
            Logger().Info("OnDeactivateAsync.");
            return Task.CompletedTask;
        }

        Task CompleteBuffer(object _)
        {
            var items = buffer.ToArray();
            buffer.Clear();

            foreach (var item in items)
                item.TrySetResult(counter++);
            
            return Task.CompletedTask;
        }

        public override Task<int> IncrCounter()
        {
            var item = new TaskCompletionSource<int>();
            buffer.Add(item);

            return item.Task;
        }
    }
}
