using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Orleans;
using UnitTests.GrainInterfaces;
namespace UnitTests.Grains
{
    public class Initialized_State : GrainState
    {
        public List<string> Names { get; set; }
        public Initialized_State()
        {
            Names = new List<string>();
        }
    }
    public class InitialStateGrain : Grain<Initialized_State>, IInitialStateGrain
    {
        public Task<List<string>> GetNames()
        {
            return Task.FromResult(State.Names);
        }
    }
}