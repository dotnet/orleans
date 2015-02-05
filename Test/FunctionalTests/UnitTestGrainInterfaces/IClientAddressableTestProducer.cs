using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Orleans;

namespace UnitTestGrainInterfaces
{
    [Factory(FactoryAttribute.FactoryTypes.ClientObject)]
    public interface IClientAddressableTestProducer : IGrain
    {
        Task<int> Poll();
    }
}
