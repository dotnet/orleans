using Orleans;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace UnitTests.Grains
{
    interface IFactory<TInput, TOutput>
    {
        Task<TOutput> Product(TInput input);
    }

    class Factory
    {
        public int SomeProperty { get; set; }
    }

    class AFactory : Factory, IFactory<int, double>
    {
        async Task<double> IFactory<int, double>.Product(int input)
        {
            await Task.Delay(100);
            return input;
        }
    }
}
