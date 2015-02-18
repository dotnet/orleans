using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Orleans;

using System.Collections;

namespace BenchmarkGrains
{

    public interface IChainedGrain : IGrain
    {
        Task<int> GetId();
        Task<int> GetX();
        Task<IChainedGrain> GetNext();
        //[ReadOnly]
        Task<int> GetCalculatedValue();
        Task SetNext(IChainedGrain next);
        //[ReadOnly]
        Task Validate(bool nextIsSet);
        Task PassThis(IChainedGrain next);
    }
}
