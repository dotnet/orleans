using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Orleans;

namespace MultifacetGrain
{
    public interface IMultifacetFactoryTestGrain : IGrain
    {
        Task<IMultifacetReader> GetReader(IMultifacetTestGrain grain);
        Task<IMultifacetReader> GetReader();
        Task<IMultifacetWriter> GetWriter(IMultifacetTestGrain grain);
        Task<IMultifacetWriter> GetWriter();
        Task SetReader(IMultifacetReader reader);
        Task SetWriter(IMultifacetWriter writer);
    }
}