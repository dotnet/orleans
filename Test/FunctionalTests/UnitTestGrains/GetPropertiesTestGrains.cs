using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnitTestGrainInterfaces;
using Orleans;

namespace UnitTestGrains
{
    public interface IComplexGrainState : IGrainState
    {
        ComplicatedTestType<int> FldInt { get; set; }
        ComplicatedTestType<string> FldStr { get; set; }
    }
    
    public class ComplexGrain : Grain<IComplexGrainState>, IComplexGrain
    {
        public Task SeedFldInt(int i)
        {
            State.FldInt.InitWithSeed(i);
            return TaskDone.Done;
        }
        public Task SeedFldStr(string s)
        {
            State.FldStr.InitWithSeed(s);
            return TaskDone.Done;
        }
        public override Task OnActivateAsync()
        {
            State.FldInt = new ComplicatedTestType<int>();
            State.FldStr = new ComplicatedTestType<string>();
            return TaskDone.Done;
        }

        public Task<ComplicatedTestType<int>> GetFldInt()
        {
           return Task.FromResult(State.FldInt);
        }

        public Task<ComplicatedTestType<string>> GetFldStr()
        {
            return Task.FromResult(State.FldStr);
        }
    }
    public interface ILinkedListGrainState : IGrainState
    {
        ILinkedListGrain Next { get; set; }
        int Value { get; set; }
    }
    public class LinkedListGrain : Grain<ILinkedListGrainState>, ILinkedListGrain
    {
        public Task SetValue(int v)
        {
            State.Value = v;
            return TaskDone.Done;
        }
        public Task SetNext(ILinkedListGrain next)
        {
            State.Next = next;
            return TaskDone.Done;
        }

        public Task<ILinkedListGrain> GetNext()
        {
            return Task.FromResult(State.Next);
        }

        public Task<int> GetValue()
        {
            return Task.FromResult(State.Value);
        }
    }

}
