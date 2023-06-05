using Microsoft.Extensions.Logging;
using Orleans.Providers;
using Orleans.Runtime;
using UnitTests.GrainInterfaces;

namespace UnitTests.Grains
{
    [Serializable]
    [GenerateSerializer]
    public class ChainedGrainState
    {
        [Id(0)]
        public int Id { get; set; }

        [Id(1)]
        public int X { get; set; }

        [Id(2)]
        public IChainedGrain Next { get; set; }
    }

    /// <summary>
    /// A simple grain that allows to set two arguments and then multiply them.
    /// </summary>
    [StorageProvider(ProviderName = "MemoryStore")]
    public class ChainedGrain : Grain<ChainedGrainState>, IChainedGrain
    {
        private ILogger logger;

        public ChainedGrain(ILoggerFactory loggerFactory)
        {
            logger = loggerFactory.CreateLogger($"{GetType().Name}-{IdentityString}");
        }

        Task<IChainedGrain> IChainedGrain.GetNext() => Task.FromResult(State.Next);

        Task<int> IChainedGrain.GetId() => Task.FromResult(State.Id);

        Task<int> IChainedGrain.GetX() => Task.FromResult(State.X);

        public async Task<int> GetCalculatedValue()
        {
            if (State.Next == null)
            {/*
                if (Id % 10 != 0)
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine(String.Format("Id={0}, Next=null, X={1}", Id, X));
                    Console.ResetColor();
                }*/
                return State.X;
            }
            var nextValue = await State.Next.GetCalculatedValue();
            return State.X + nextValue;
        }

        public Task SetNext(IChainedGrain next)
        {
            State.Next = next;
            return Task.CompletedTask;
        }

        public Task SetNextNested(ChainGrainHolder next)
        {
            State.Next = next.Next;
            return Task.CompletedTask;
        }

        public Task Validate(bool nextIsSet)
        {
            if ((nextIsSet && State.Next != null) || (!nextIsSet && State.Next == null))
            {
                // logger.Verbose("Id={0} validated successfully: Next={1}", State.Id, State.Next);
                return Task.CompletedTask;
            }

            logger.LogWarning("ChainGrain Id={Id} is in an invalid state. Next={Next}", State.Id, State.Next);
            throw new OrleansException($"ChainGrain Id={State.Id} is in an invalid state. Next={State.Next}");
        }

        public Task PassThis(IChainedGrain next) => next.SetNext(this);

        public Task PassNull(IChainedGrain next) => next.SetNext(null);

        public Task PassThisNested(ChainGrainHolder next) => next.Next.SetNextNested(new ChainGrainHolder { Next = this });

        public Task PassNullNested(ChainGrainHolder next) => next.Next.SetNextNested(new ChainGrainHolder { Next = null });
    }
}
