using Microsoft.Extensions.Logging;
using Orleans.Streams;
using Orleans.Streams.Core;
using UnitTests.GrainInterfaces;

namespace UnitTests.Grains
{
    public class Passive_ConsumerGrain : Grain, IPassive_ConsumerGrain, IStreamSubscriptionObserver
    {
        internal ILogger logger;
        private List<ICounterObserver> consumerObservers;
        private List<StreamSubscriptionHandle<IFruit>> consumerHandles;
        private int onAddCalledCount;

        public Passive_ConsumerGrain(ILoggerFactory loggerFactory)
        {
            logger = loggerFactory.CreateLogger($"{GetType().Name}-{IdentityString}");
        }

        public override Task OnActivateAsync(CancellationToken cancellationToken)
        {
            logger.LogInformation("OnActivateAsync");
            onAddCalledCount = 0;
            consumerObservers = new List<ICounterObserver>();
            consumerHandles = new List<StreamSubscriptionHandle<IFruit>>();
            return Task.CompletedTask;
        }

        public Task<int> GetCountOfOnAddFuncCalled() => Task.FromResult(onAddCalledCount);

        public Task<int> GetNumberConsumed()
        {
            var sum = 0;
            foreach (var observer in consumerObservers)
            {
                sum += observer.NumConsumed;
            }

            logger.LogInformation("GetNumberConsumed {Sum}", sum);
            return Task.FromResult(sum);
        }

        public async Task StopConsuming()
        {
            logger.LogInformation("StopConsuming");
            foreach (var handle in consumerHandles)
            {
                await handle.UnsubscribeAsync();
            }
            consumerHandles.Clear();
            consumerObservers.Clear();
        }

        public override Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
        {
            logger.LogInformation("OnDeactivateAsync");
            return Task.CompletedTask;
        }

        public async Task OnSubscribed(IStreamSubscriptionHandleFactory handleFactory)
        {
            logger.LogInformation("OnAdd");
            onAddCalledCount++;
            var observer = new CounterObserver<IFruit>(logger);
            var newhandle = handleFactory.Create<IFruit>();
            consumerHandles.Add(await newhandle.ResumeAsync(observer));
            consumerObservers.Add(observer);
        }
    }

    public class Jerk_ConsumerGrain : Grain, IJerk_ConsumerGrain, IStreamSubscriptionObserver
    {
        internal ILogger logger;

        public Jerk_ConsumerGrain(ILoggerFactory loggerFactory)
        {
            logger = loggerFactory.CreateLogger($"{GetType().Name}-{IdentityString}");
        }

        public override Task OnActivateAsync(CancellationToken cancellationToken)
        {
            logger.LogInformation("OnActivateAsync");
            return Task.CompletedTask;
        }

        //Jerk_ConsumerGrai would unsubscrube on any subscription added to it
        public async Task OnSubscribed(IStreamSubscriptionHandleFactory handleFactory)
        {
            var handle = handleFactory.Create<int>();
            await handle.UnsubscribeAsync();
        }
    }

    public interface ICounterObserver
    {
        int NumConsumed { get; }
    }

    public class CounterObserver<T> : IAsyncObserver<T>, ICounterObserver
    {
        public int NumConsumed { get; private set; }
        private ILogger logger;
        internal CounterObserver(ILogger logger)
        {
            NumConsumed = 0;
            this.logger = logger;
        }

        public Task OnNextAsync(T item, StreamSequenceToken token = null)
        {
            NumConsumed++;
            logger.LogInformation("Consumer {HashCode} OnNextAsync() received item {Item}, with NumConsumed {NumConsumed}", GetHashCode(), item, NumConsumed);
            return Task.CompletedTask;
        }

        public Task OnCompletedAsync()
        {
            logger.LogInformation("Consumer {HashCode} OnCompletedAsync()", GetHashCode());
            return Task.CompletedTask;
        }

        public Task OnErrorAsync(Exception ex)
        {
            logger.LogInformation(ex, "Consumer {HashCode} OnErrorAsync()", GetHashCode());
            return Task.CompletedTask;
        }
    }

    [ImplicitStreamSubscription(StreamNameSpace)]
    [ImplicitStreamSubscription(StreamNameSpace2)]
    public class ImplicitSubscribeGrain : Passive_ConsumerGrain, IImplicitSubscribeGrain
    {
        public const string StreamNameSpace = "ImplicitSubscriptionSpace11";
        public const string StreamNameSpace2 = "ImplicitSubscriptionSpace22";

        public ImplicitSubscribeGrain(ILoggerFactory loggerFactory) : base(loggerFactory)
        {
        }
    }
}
