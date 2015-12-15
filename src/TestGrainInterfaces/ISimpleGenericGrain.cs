using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Orleans;

namespace UnitTests.GrainInterfaces
{
    public interface ISimpleGenericGrain<T> : IGrainWithIntegerKey
    {
        Task Set(T t);

        Task Transform();

        Task<T> Get();

        Task CompareGrainReferences(ISimpleGenericGrain<T> clientRef);
    }
}
