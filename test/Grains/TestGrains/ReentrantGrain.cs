using Microsoft.Extensions.Logging;
using Orleans.Concurrency;
using Orleans.Serialization.Invocation;
using Orleans.Streams;

using UnitTests.GrainInterfaces;

namespace UnitTests.Grains
{
    [Reentrant]
    public class ReentrantGrain : Grain, IReentrantGrain
    {
        private IReentrantGrain Self { get; set; }

        public Task<string> One()
        {
            return Task.FromResult("one");
        }

        public async Task<string> Two()
        {
            return await Self.One() + " two";
        }

        public Task SetSelf(IReentrantGrain self)
        {
            Self = self;
            return Task.CompletedTask;
        }
    }

    public class NonRentrantGrain : Grain, INonReentrantGrain
    {
        private INonReentrantGrain Self { get; set; }

        private readonly ILogger logger;

        public NonRentrantGrain(ILoggerFactory loggerFactory)
        {
            this.logger = loggerFactory.CreateLogger($"{this.GetType().Name}-{this.IdentityString}");
        }

        public override Task OnActivateAsync(CancellationToken cancellationToken)
        {
            logger.LogInformation("OnActivateAsync");
            return base.OnActivateAsync(cancellationToken);
        }

        public Task<string> One()
        {
            logger.LogInformation("Entering One");
            string result = "one";
            logger.LogInformation("Exiting One");
            return Task.FromResult(result);
        }

        public async Task<string> Two()
        {
            logger.LogInformation("Entering Two");
            string result = await Self.One();
            result = result + " two";
            logger.LogInformation("Exiting Two");
            return result;
        }

        public Task SetSelf(INonReentrantGrain self)
        {
            logger.LogInformation("SetSelf {Self}", self);
            Self = self;
            return Task.CompletedTask;
        }
    }

    [MayInterleave(nameof(MayInterleave))]
    public class MayInterleaveStaticPredicateGrain : Grain, IMayInterleaveStaticPredicateGrain
    {
        private readonly ILogger logger;

        public MayInterleaveStaticPredicateGrain(ILoggerFactory loggerFactory)
        {
            this.logger = loggerFactory.CreateLogger($"{this.GetType().Name}-{this.IdentityString}");
        }

        public static bool MayInterleave(IInvokable req)
        {
            // not interested
            if (req.GetArgumentCount() == 0)
                return false;

            string arg = null;

            // assume single argument message
            if (req.GetArgumentCount() == 1)
                arg = (string)UnwrapImmutable(req.GetArgument(0));

            // assume stream message
            if (req.GetArgumentCount() == 2)
                arg = (string)UnwrapImmutable(req.GetArgument(1));

            if (arg == "err")
                throw new ApplicationException("boom");

            return arg == "reentrant";
        }

        private static object UnwrapImmutable(object item) => item is Immutable<object> ? ((Immutable<object>)item).Value : item;

        private IMayInterleaveStaticPredicateGrain Self { get; set; }

        // this interleaves only when arg == "reentrant"
        // and test predicate will throw when arg = "err"
        public Task<string> One(string arg)
        {
            return Task.FromResult("one");
        }

        public async Task<string> Two()
        {
            return await Self.One("") + " two";
        }

        public async Task<string> TwoReentrant()
        {
            return await Self.One("reentrant") + " two";
        }

        public Task Exceptional()
        {
            return Self.One("err");
        }

        public async Task SubscribeToStream()
        {
            var stream = GetStream();

            await stream.SubscribeAsync((item, _) =>
            {
                logger.LogInformation("Received stream item: {Item}", item);
                return Task.CompletedTask;
            });
        }

        public Task PushToStream(string item)
        {
            return GetStream().OnNextAsync(item);
        }

        private IAsyncStream<string> GetStream() =>
            this.GetStreamProvider("sms").GetStream<string>("test-stream-interleave", Guid.Empty);

        public Task SetSelf(IMayInterleaveStaticPredicateGrain self)
        {
            Self = self;
            return Task.CompletedTask;
        }
    }

    [MayInterleave(nameof(MayInterleave))]
    public class MayInterleaveInstancedPredicateGrain : Grain, IMayInterleaveInstancedPredicateGrain
    {
        private readonly ILogger logger;

        public MayInterleaveInstancedPredicateGrain(ILoggerFactory loggerFactory)
        {
            this.logger = loggerFactory.CreateLogger($"{this.GetType().Name}-{this.IdentityString}");
        }

        public bool MayInterleave(IInvokable req)
        {
            // not interested
            if (req.GetArgumentCount() == 0)
                return false;

            string arg = null;

            // assume single argument message
            if (req.GetArgumentCount() == 1)
                arg = (string)UnwrapImmutable(req.GetArgument(0));

            // assume stream message
            if (req.GetArgumentCount() == 2)
                arg = (string)UnwrapImmutable(req.GetArgument(1));

            if (arg == "err")
                throw new ApplicationException("boom");

            return arg == "reentrant";
        }

        private static object UnwrapImmutable(object item) => item is Immutable<object> ? ((Immutable<object>)item).Value : item;

        private IMayInterleaveInstancedPredicateGrain Self { get; set; }

        // this interleaves only when arg == "reentrant"
        // and test predicate will throw when arg = "err"
        public Task<string> One(string arg)
        {
            return Task.FromResult("one");
        }

        public async Task<string> Two()
        {
            return await Self.One("") + " two";
        }

        public async Task<string> TwoReentrant()
        {
            return await Self.One("reentrant") + " two";
        }

        public Task Exceptional()
        {
            return Self.One("err");
        }

        public async Task SubscribeToStream()
        {
            var stream = GetStream();

            await stream.SubscribeAsync((item, _) =>
            {
                logger.LogInformation("Received stream item: {Item}", item);
                return Task.CompletedTask;
            });
        }

        public Task PushToStream(string item)
        {
            return GetStream().OnNextAsync(item);
        }

        private IAsyncStream<string> GetStream() =>
            this.GetStreamProvider("sms").GetStream<string>("test-stream-interleave", Guid.Empty);

        public Task SetSelf(IMayInterleaveInstancedPredicateGrain self)
        {
            Self = self;
            return Task.CompletedTask;
        }
    }

    public class UnorderedNonRentrantGrain : Grain, IUnorderedNonReentrantGrain
    {
        private IUnorderedNonReentrantGrain Self { get; set; }

        public Task<string> One()
        {
            return Task.FromResult("one");
        }

        public async Task<string> Two()
        {
            return await Self.One() + " two";
        }

        public Task SetSelf(IUnorderedNonReentrantGrain self)
        {
            Self = self;
            return Task.CompletedTask;
        }
    }
    [Reentrant]
    public class ReentrantSelfManagedGrain1 : Grain, IReentrantSelfManagedGrain
    {
        private long destination;
        private readonly ILogger logger;

        public ReentrantSelfManagedGrain1(ILoggerFactory loggerFactory)
        {
            this.logger = loggerFactory.CreateLogger($"{this.GetType().Name}-{this.IdentityString}");
        }

        public override Task OnActivateAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task<int> GetCounter()
        {
            return Task.FromResult(1);
        }

        public Task SetDestination(long id)
        {
            destination = id;
            return Task.CompletedTask;
        }

        public Task Ping(int seconds)
        {
            logger.LogInformation("Start Ping({Seconds})", seconds);
            var start = DateTime.UtcNow;
            var end = start + TimeSpan.FromSeconds(seconds);
            int foo = 0;
            while (DateTime.UtcNow < end)
            {
                foo++;
                if (foo > 100000)
                    foo = 0;
            }

            logger.LogInformation("Before GetCounter - OtherId={OtherId}", destination);
            IReentrantSelfManagedGrain otherGrain = GrainFactory.GetGrain<IReentrantSelfManagedGrain>(destination);
            var ctr = otherGrain.GetCounter();
            logger.LogInformation("After GetCounter() - returning promise");
            return ctr;
        }
    }

    public class NonReentrantSelfManagedGrain1 : Grain, INonReentrantSelfManagedGrain
    {
        private long destination;
        private readonly ILogger logger;

        public NonReentrantSelfManagedGrain1(ILoggerFactory loggerFactory)
        {
            this.logger = loggerFactory.CreateLogger($"{this.GetType().Name}-{this.IdentityString}");
        }

        public override Task OnActivateAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task<int> GetCounter()
        {
            return Task.FromResult(1);
        }

        public Task SetDestination(long id)
        {
            destination = id;
            return Task.CompletedTask;
        }

        public Task Ping(int seconds)
        {
            logger.LogInformation("Start Ping({Seconds})", seconds);
            var start = DateTime.UtcNow;
            var end = start + TimeSpan.FromSeconds(seconds);
            int foo = 0;
            while (DateTime.UtcNow < end)
            {
                foo++;
                if (foo > 100000)
                    foo = 0;
            }

            logger.LogInformation("Before GetCounter - OtherId={OtherId}", destination);
            INonReentrantSelfManagedGrain otherGrain = GrainFactory.GetGrain<INonReentrantSelfManagedGrain>(destination);
            var ctr = otherGrain.GetCounter();
            logger.LogInformation("After GetCounter() - returning promise");
            return ctr;
        }
    }

    [Reentrant]
    public class FanOutGrain : Grain, IFanOutGrain
    {
        private readonly ILogger logger;
        private static readonly TimeSpan OneSecond = TimeSpan.FromSeconds(1);

        public FanOutGrain(ILoggerFactory loggerFactory)
        {
            this.logger = loggerFactory.CreateLogger($"{this.GetType().Name}-{this.IdentityString}");
        }

        public override Task OnActivateAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public async Task FanOutReentrant(int offset, int num)
        {
            IReentrantTaskGrain[] fanOutGrains = await InitTaskGrains_Reentrant(offset, num);

            logger.LogInformation("Starting fan-out calls to {Count} grains", num);
            List<Task> promises = new List<Task>();
            for (int i = 0; i < num; i++)
            {
                //Task promise = fanOutGrains[i].Ping(OneSecond);
                Task promise = fanOutGrains[i].GetCounter();
                promises.Add(promise);
            }
            logger.LogInformation("Waiting for responses from {Count} grains with offset={Offset}", num, offset);
            await Task.WhenAll(promises);
            logger.LogInformation("Received {Count} responses", num);
        }

        public async Task FanOutNonReentrant(int offset, int num)
        {
            INonReentrantTaskGrain[] fanOutGrains = await InitTaskGrains_NonReentrant(offset, num);

            logger.LogInformation("Starting fan-out calls to {Count} grains", num);
            List<Task> promises = new List<Task>();
            for (int i = 0; i < num; i++)
            {
                //Task promise = fanOutGrains[i].Ping(OneSecond);
                Task promise = fanOutGrains[i].GetCounter();
                promises.Add(promise);
            }
            logger.LogInformation("Waiting for responses from {Count} grains", num);
            await Task.WhenAll(promises);
            logger.LogInformation("Received {Count} responses", num);
        }

        public async Task FanOutReentrant_Chain(int offset, int num)
        {
            IReentrantTaskGrain[] fanOutGrains = await InitTaskGrains_Reentrant(offset, num);

            logger.LogInformation("Starting fan-out chain calls to {Count} grains", num);
            List<Task> promises = new List<Task>();
            for (int i = 0; i < num; i++)
            {
                Task promise = fanOutGrains[i].Ping(OneSecond);
                promises.Add(promise);
            }
            logger.LogInformation("Waiting for responses from {Count} grains with offset={offset}", num, offset);
            await Task.WhenAll(promises);
            logger.LogInformation("Received {Count} responses", num);
        }

        public async Task FanOutNonReentrant_Chain(int offset, int num)
        {
            INonReentrantTaskGrain[] fanOutGrains = await InitTaskGrains_NonReentrant(offset, num);

            logger.LogInformation("Starting fan-out chain calls to {Count} grains", num);
            List<Task> promises = new List<Task>();
            for (int i = 0; i < num; i++)
            {
                Task promise = fanOutGrains[i].Ping(OneSecond);
                promises.Add(promise);
            }
            logger.LogInformation("Waiting for responses from {Count} grains", num);
            await Task.WhenAll(promises);
            logger.LogInformation("Received {Count} responses", num);
        }

        private async Task<IReentrantTaskGrain[]> InitTaskGrains_Reentrant(int offset, int num)
        {
            IReentrantTaskGrain[] fanOutGrains = new IReentrantTaskGrain[num];

            logger.LogInformation("Creating {Count} fan-out reentrant worker grains", num);
            List<Task> promises = new List<Task>();
            for (int i = 0; i < num; i++)
            {
                int idx = offset + i;
                IReentrantTaskGrain grain = GrainFactory.GetGrain<IReentrantTaskGrain>(idx);
                fanOutGrains[i] = grain;
                int next = offset + ((i + 1) % num);
                Task promise = grain.SetDestination(next);
                promises.Add(promise);
            }
            await Task.WhenAll(promises);
            return fanOutGrains;
        }
        private async Task<INonReentrantTaskGrain[]> InitTaskGrains_NonReentrant(int offset, int num)
        {
            INonReentrantTaskGrain[] fanOutGrains = new INonReentrantTaskGrain[num];

            logger.LogInformation("Creating {Count} fan-out non-reentrant worker grains", num);
            List<Task> promises = new List<Task>();
            for (int i = 0; i < num; i++)
            {
                int idx = offset + i;
                INonReentrantTaskGrain grain = GrainFactory.GetGrain<INonReentrantTaskGrain>(idx);
                fanOutGrains[i] = grain;
                int next = offset + ((i + 1) % num);
                Task promise = grain.SetDestination(next);
                promises.Add(promise);
            }
            await Task.WhenAll(promises);
            return fanOutGrains;
        }
    }

    [Reentrant]
    public class FanOutACGrain : Grain, IFanOutACGrain
    {
        private readonly ILogger logger;
        private static readonly TimeSpan OneSecond = TimeSpan.FromSeconds(1);

        public FanOutACGrain(ILoggerFactory loggerFactory)
        {
            this.logger = loggerFactory.CreateLogger($"{this.GetType().Name}-{this.IdentityString}");
        }

        public override Task OnActivateAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public async Task FanOutACReentrant(int offset, int num)
        {
            IReentrantSelfManagedGrain[] fanOutGrains = await InitACGrains_Reentrant(offset, num);

            logger.LogInformation("Starting fan-out calls to {Count} grains", num);
            List<Task> promises = new List<Task>();
            for (int i = 0; i < num; i++)
            {
                Task promise = fanOutGrains[i].GetCounter();
                promises.Add(promise);
            }
            logger.LogInformation("Waiting for responses from {Count} grains", num);
            await Task.WhenAll(promises);
            logger.LogInformation("Received {Count} responses", num);
        }

        public async Task FanOutACNonReentrant(int offset, int num)
        {
            INonReentrantSelfManagedGrain[] fanOutGrains = await InitACGrains_NonReentrant(offset, num);

            logger.LogInformation("Starting fan-out calls to {Count} grains", num);
            List<Task> promises = new List<Task>();
            for (int i = 0; i < num; i++)
            {
                Task promise = fanOutGrains[i].GetCounter();
                promises.Add(promise);
            }
            logger.LogInformation("Waiting for responses from {Count} grains", num);
            await Task.WhenAll(promises);
            logger.LogInformation("Received {Count} responses", num);
        }

        public async Task FanOutACReentrant_Chain(int offset, int num)
        {
            IReentrantSelfManagedGrain[] fanOutGrains = await InitACGrains_Reentrant(offset, num);

            logger.LogInformation("Starting fan-out calls to {Count} grains", num);
            List<Task> promises = new List<Task>();
            for (int i = 0; i < num; i++)
            {
                Task promise = fanOutGrains[i].Ping(OneSecond.Seconds);
                promises.Add(promise);
            }
            logger.LogInformation("Waiting for responses from {Count} grains", num);
            await Task.WhenAll(promises);
            logger.LogInformation("Received {Count} responses", num);
        }

        public async Task FanOutACNonReentrant_Chain(int offset, int num)
        {
            INonReentrantSelfManagedGrain[] fanOutGrains = await InitACGrains_NonReentrant(offset, num);

            logger.LogInformation("Starting fan-out calls to {Count} grains", num);
            List<Task> promises = new List<Task>();
            for (int i = 0; i < num; i++)
            {
                Task promise = fanOutGrains[i].Ping(OneSecond.Seconds);
                promises.Add(promise);
            }
            logger.LogInformation("Waiting for responses from {Count} grains", num);
            await Task.WhenAll(promises);
            logger.LogInformation("Received {Count} responses", num);
        }

        private async Task<IReentrantSelfManagedGrain[]> InitACGrains_Reentrant(int offset, int num)
        {
            var fanOutGrains = new IReentrantSelfManagedGrain[num];
            List<Task> promises = new List<Task>();
            logger.LogInformation("Creating {Count} fan-out reentrant worker grains with offset={Offset}", num, offset);
            for (int i = 0; i < num; i++)
            {
                int idx = offset + i;
                var grain = GrainFactory.GetGrain<IReentrantSelfManagedGrain>(idx);
                fanOutGrains[i] = grain;
                int next = offset + ((i + 1) % num);
                Task promise = grain.SetDestination(next);
                promises.Add(promise);
            }
            await Task.WhenAll(promises);
            return fanOutGrains;
        }

        private async Task<INonReentrantSelfManagedGrain[]> InitACGrains_NonReentrant(int offset, int num)
        {
            var fanOutGrains = new INonReentrantSelfManagedGrain[num];
            List<Task> promises = new List<Task>();
            logger.LogInformation("Creating {Count} fan-out non-reentrant worker grains with offset={offset}", num, offset);
            for (int i = 0; i < num; i++)
            {
                int idx = offset + i;
                var grain = GrainFactory.GetGrain<INonReentrantSelfManagedGrain>(idx);
                fanOutGrains[i] = grain;
                int next = offset + ((i + 1) % num);
                Task promise = grain.SetDestination(next);
                promises.Add(promise);
            }
            await Task.WhenAll(promises);
            return fanOutGrains;
        }
    }

    [Reentrant]
    public class ReentrantTaskGrain : Grain, IReentrantTaskGrain
    {
        private readonly ILogger logger;
        private long otherId;
        private int count;

        public ReentrantTaskGrain(ILoggerFactory loggerFactory)
        {
            this.logger = loggerFactory.CreateLogger($"{this.GetType().Name}-{this.IdentityString}");
        }

        public override Task OnActivateAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task SetDestination(long id)
        {
            otherId = id;
            return Task.CompletedTask;
        }

        public async Task Ping(TimeSpan wait)
        {
            logger.LogInformation("Ping Delay={Delay}", wait);
            await Task.Delay(wait);
            logger.LogInformation("Before GetCounter - OtherId={OtherId}", otherId);
            var otherGrain = GrainFactory.GetGrain<IReentrantTaskGrain>(otherId);
            var ctr = await otherGrain.GetCounter();
            logger.LogInformation("After GetCounter() - got value={Counter}", ctr);
        }

        public Task<int> GetCounter()
        {
            return Task.FromResult(++count);
        }
    }

    public class NonReentrantTaskGrain : Grain, INonReentrantTaskGrain
    {
        private readonly ILogger logger;
        private long otherId;
        private int count;

        public NonReentrantTaskGrain(ILoggerFactory loggerFactory)
        {
            this.logger = loggerFactory.CreateLogger($"{this.GetType().Name}-{this.IdentityString}");
        }

        public override Task OnActivateAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task SetDestination(long id)
        {
            otherId = id;
            return Task.CompletedTask;
        }

        public async Task Ping(TimeSpan wait)
        {
            logger.LogInformation("Ping Delay={Delay}", wait);
            await Task.Delay(wait);
            logger.LogInformation("Before GetCounter - OtherId={OtherId}", otherId);
            var otherGrain = GrainFactory.GetGrain<INonReentrantTaskGrain>(otherId);
            var ctr = await otherGrain.GetCounter();
            logger.LogInformation("After GetCounter() - got value={Counter}", ctr);
        }

        public Task<int> GetCounter()
        {
            return Task.FromResult(++count);
        }
    }
}
