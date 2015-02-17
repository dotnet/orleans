using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Orleans;
using Orleans.Runtime;

using System.Collections;


namespace BenchmarkGrains
{
    public interface IChainedGrainState : IGrainState
    {
        int Id { get; set; }

        int X { get; set; }

        IChainedGrain Next { get; set; }
    }

    /// <summary>
    /// A simple grain that allows to set two agruments and then multiply them.
    /// </summary>
    public class ChainedGrain : Grain<IChainedGrainState>, IChainedGrain
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
