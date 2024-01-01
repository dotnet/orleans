using Microsoft.Extensions.Logging;
using Orleans.Utilities;
using UnitTests.GrainInterfaces;

namespace UnitTests.Grains
{
    public class SimpleObserverableGrain : Grain, ISimpleObserverableGrain
    {
        private readonly ILogger logger;
        internal int A { get; set; }
        internal int B { get; set; }
        internal int EventDelay { get; set; }
        internal ObserverManager<ISimpleGrainObserver> Observers { get; set; }

        public SimpleObserverableGrain(ILoggerFactory loggerFactory)
        {
            EventDelay = 1000;
            logger = loggerFactory.CreateLogger( $"{nameof(SimpleObserverableGrain)}-{base.IdentityString}-{base.RuntimeIdentity}");
            this.Observers = new ObserverManager<ISimpleGrainObserver>(TimeSpan.FromMinutes(5), logger);
        }

        public override Task OnActivateAsync(CancellationToken cancellationToken)
        {
            logger.LogInformation("Activate.");
            return Task.CompletedTask;
        }

        public async Task SetA(int a)
        {
            logger.LogInformation("SetA={A}", a);
            A = a;

            //If this were run with Task.Run there were no need for the added Unwrap call.
            //However, Task.Run runs in ThreadPool and not in Orleans TaskScheduler, unlike Task.Factory.StartNew.
            //See more at https://learn.microsoft.com/dotnet/orleans/grains/external-tasks-and-grains.
            //The extra task comes from the internal asynchronous lambda due to Task.Delay. For deeper
            //insight, see at http://blogs.msdn.com/b/pfxteam/archive/2012/02/08/10265476.aspx.
            await Task.Factory.StartNew(async () =>
            {
                await Task.Delay(EventDelay);
                RaiseStateUpdateEvent();
            }).Unwrap();
        }

        public async Task SetB(int b)
        {
            this.B = b;

            //If this were run with Task.Run there were no need for the added Unwrap call.
            //However, Task.Run runs in ThreadPool and not in Orleans TaskScheduler, unlike Task.Factory.StartNew.
            //See more at https://learn.microsoft.com/dotnet/orleans/grains/external-tasks-and-grains.
            //The extra task comes from the internal asynchronous lambda due to Task.Delay. For deeper
            //insight, see at http://blogs.msdn.com/b/pfxteam/archive/2012/02/08/10265476.aspx.
            await Task.Factory.StartNew(async () =>
            {
                await Task.Delay(EventDelay);
                RaiseStateUpdateEvent();
            }).Unwrap();
        }

        public async Task IncrementA()
        {
            await SetA(A + 1);
        }

        public Task<int> GetAxB()
        {
            return Task.FromResult(A * B);
        }

        public Task<int> GetAxB(int a, int b)
        {
            return Task.FromResult(a * b);
        }

        public Task<int> GetA()
        {
            return Task.FromResult(A);
        }

        public Task Subscribe(ISimpleGrainObserver observer)
        {
            Observers.Subscribe(observer, observer);
            return Task.CompletedTask;
        }

        public Task Unsubscribe(ISimpleGrainObserver observer)
        {
            Observers.Unsubscribe(observer);
            return Task.CompletedTask;
        }

        public Task<string> GetRuntimeInstanceId()
        {
            return Task.FromResult(RuntimeIdentity);
        }

        protected void RaiseStateUpdateEvent()
        {
            Observers.Notify((ISimpleGrainObserver observer) =>
            {
                observer.StateChanged(A, B);
            });
        }
    }
}
