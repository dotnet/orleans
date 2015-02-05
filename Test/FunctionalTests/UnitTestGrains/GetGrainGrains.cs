using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Orleans;

namespace UnitTestGrains
{
    public class BaseGrain : Grain, IBase
    {
        public Task<bool> Foo()
        {
            return Task.FromResult(true);
        }
    }

    public class DerivedFromBaseGrain : Grain, IDerivedFromBase
    {
        public Task<bool> Bar()
        {
            return Task.FromResult(true);
        }

        public Task<bool> Foo()
        {
            return Task.FromResult(false);
        }
    }

    public class BaseGrain1 : Grain, IBase1
    {
        public Task<bool> Foo()
        {
            return Task.FromResult(false);
        }
    }

    public class BaseGrain1And2 : Grain, IBase3, IBase2
    {
        public Task<bool> Foo()
        {
            return Task.FromResult(false);
        }

        public Task<bool> Bar()
        {
            return Task.FromResult(true);
        }
    }

    public class Base4 : Grain, IBase4
    {
        public Task<bool> Foo()
        {
            return Task.FromResult(false);
        }
    }

    public class Base4_ : Grain, IBase4
    {
        public Task<bool> Foo()
        {
            return Task.FromResult(true);
        }
    }

    public class StringGrain : Grain, IStringGrain
    {
        public Task<bool> Foo()
        {
            return Task.FromResult(true);
        }
    }

    public class GuidGrain : Grain, IGuidGrain
    {
        public Task<bool> Foo()
        {
            return Task.FromResult(true);
        }
    }
}
