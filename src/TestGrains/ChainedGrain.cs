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
            return TaskDone.Done;
        }


        public Task Validate(bool nextIsSet)
        {
            if ((nextIsSet && State.Next != null) || (!nextIsSet && State.Next == null))
            {
                //Console.ForegroundColor = ConsoleColor.Green;
                //Console.WriteLine(String.Format("Id={0} validated successfully: Next={1}", Id, Next));
                //Console.ResetColor();
                return TaskDone.Done;
            }

            string msg = String.Format("ChainGrain Id={0} is in an invalid state. Next={1}", State.Id, State.Next);
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine(msg);
            Console.ResetColor();
            throw new OrleansException(msg);
        }

        public Task PassThis(IChainedGrain next)
        {
            return next.SetNext(this);
        }

        #endregion
    }
}
