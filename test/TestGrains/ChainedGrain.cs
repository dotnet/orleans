using System;
using System.Threading.Tasks;
using Orleans;
using Orleans.Providers;
using Orleans.Runtime;
using UnitTests.GrainInterfaces;

namespace UnitTests.Grains
{
    [Serializable]
    public class ChainedGrainState
    {
        public int Id { get; set; }

        public int X { get; set; }

        public IChainedGrain Next { get; set; }
    }

    /// <summary>
    /// A simple grain that allows to set two agruments and then multiply them.
    /// </summary>
    [StorageProvider(ProviderName = "MemoryStore")]
    public class ChainedGrain : Grain<ChainedGrainState>, IChainedGrain
    {
        private Logger logger;

        public override Task OnActivateAsync()
        {
            logger = GetLogger("ChainedGrain-" + IdentityString);
            return base.OnActivateAsync();
        }

        #region IChainedGrain Members

        Task<IChainedGrain> IChainedGrain.GetNext() { return Task.FromResult(State.Next); } 

        Task<int> IChainedGrain.GetId() { return Task.FromResult(State.Id); }

        Task<int> IChainedGrain.GetX() { return Task.FromResult(State.X); }

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
            int nextValue = await State.Next.GetCalculatedValue();
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

            string msg = String.Format("ChainGrain Id={0} is in an invalid state. Next={1}", State.Id, State.Next);
            logger.Warn(0, msg);
            throw new OrleansException(msg);
        }

        public Task PassThis(IChainedGrain next)
        {
            return next.SetNext(this);
        }

        public Task PassNull(IChainedGrain next)
        {
            return next.SetNext(null);
        }

        public Task PassThisNested(ChainGrainHolder next)
        {
            return next.Next.SetNextNested(new ChainGrainHolder { Next = this });
        }

        public Task PassNullNested(ChainGrainHolder next)
        {
            return next.Next.SetNextNested(new ChainGrainHolder { Next = null });
        }

        #endregion
    }
}
