using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Orleans;

namespace UnitTestGrains
{
    public interface IErrorGrainWithAsyncMethods : IErrorGrain
    {
        Task IncrementAAsync_1();
    }
}
