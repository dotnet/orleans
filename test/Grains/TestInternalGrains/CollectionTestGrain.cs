using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Concurrency;
using Orleans.Runtime;
using UnitTests.GrainInterfaces;

namespace UnitTests.Grains
{
    public class CollectionTestGrain : Grain, ICollectionTestGrain
    {
        protected readonly IGrainContext _grainContext;
        private DateTime activated;

        private ICollectionTestGrain other;
        private ILogger logger;
        private int counter;
        private static int staticCounter;

        public CollectionTestGrain(IGrainContext grainContext)
        {
            _grainContext = grainContext;
        }

        protected virtual ILogger Logger()
        {
            return logger;
        }

        public override Task OnActivateAsync(CancellationToken cancellationToken)
        {
            logger = this.ServiceProvider.GetRequiredService<ILoggerFactory>()
                .CreateLogger(string.Format("CollectionTestGrain {0} {1} on {2}.", GrainId, _grainContext.ActivationId, RuntimeIdentity));
            logger.LogInformation("OnActivateAsync.");
            activated = DateTime.UtcNow;
            counter = 0;
            return Task.CompletedTask;
        }

        public override Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
        {
            Logger().LogInformation("OnDeactivateAsync.");
            return Task.CompletedTask;
        }

        public virtual Task<int> IncrCounter()
        {
            staticCounter++;
            counter++;
            int tmpCounter = counter;
            Logger().LogInformation("IncrCounter {Counter}, staticCounter {StaticCounter}.", tmpCounter, staticCounter);
            return Task.FromResult(counter);
        }

        public Task<TimeSpan> GetAge()
        {
            Logger().LogInformation("GetAge.");
            return Task.FromResult(DateTime.UtcNow.Subtract(activated));
        }

        public virtual Task DeactivateSelf()
        {
            Logger().LogInformation("DeactivateSelf.");
            DeactivateOnIdle();
            return Task.CompletedTask;
        }

        public Task SetOther(ICollectionTestGrain other)
        {
            Logger().LogInformation("SetOther.");
            this.other = other;
            return Task.CompletedTask;
        }

        public Task<TimeSpan> GetOtherAge()
        {
            Logger().LogInformation("GetOtherAge.");
            return other.GetAge();
        }

        public Task<string> GetRuntimeInstanceId()
        {
            Logger().LogInformation("GetRuntimeInstanceId.");
            return Task.FromResult(RuntimeIdentity);
        }

        public Task<ICollectionTestGrain> GetGrainReference()
        {
            Logger().LogInformation("GetGrainReference.");
            return Task.FromResult(this.AsReference<ICollectionTestGrain>());
        }
        public Task StartTimer(TimeSpan timerPeriod, TimeSpan delayPeriod)
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
            Logger().LogInformation("Start TimerCallback {Counter}, staticCounter {StaticCounter}.", tmpCounter, staticCounter);
            await Task.Delay(delayPeriod);
            Logger().LogInformation("After first delay TimerCallback {Counter}, staticCounter {StaticCounter}.", tmpCounter, staticCounter);
            await Task.Delay(delayPeriod);
            Logger().LogInformation("After second delay TimerCallback {Counter}, staticCounter {StaticCounter}.", tmpCounter, staticCounter);
        }
    }

    [Reentrant]
    public class ReentrantCollectionTestGrain : CollectionTestGrain, ICollectionTestGrain
    {
        private ILogger logger;
        private int counter;
        private static int staticCounter;

        public ReentrantCollectionTestGrain(IGrainContext grainContext) : base(grainContext)
        {
        }

        protected override ILogger Logger()
        {
            return logger;
        }

        public override Task OnActivateAsync(CancellationToken cancellationToken)
        {
            logger = this.ServiceProvider.GetRequiredService<ILoggerFactory>()
                .CreateLogger($"CollectionTestGrain {GrainId} {_grainContext.ActivationId} on {RuntimeIdentity}.");
            logger.LogInformation("OnActivateAsync.");
            counter = 0;
            return Task.CompletedTask;
        }

        public override Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
        {
            Logger().LogInformation("OnDeactivateAsync.");
            return Task.CompletedTask;
        }

        public override async Task<int> IncrCounter()
        {
            staticCounter++;
            int tmpCounter = counter++;
            Logger().LogInformation("Reentrant:IncrCounter BEFORE Delay {Count}, staticCounter {StaticCounter}.", tmpCounter, staticCounter);
            await Task.Delay(TimeSpan.FromMilliseconds(1000));
            Logger().LogInformation("Reentrant:IncrCounter AFTER Delay {Count}, staticCounter {StaticCounter}.", tmpCounter, staticCounter);
            return counter;
        }
    }
}
