using System;
using System.Threading.Tasks;
using Orleans;
using UnitTests.GrainInterfaces;

namespace UnitTests.Grains
{
    [Serializable]
    public class MultifacetFactoryTestGrainState
    {
        public IMultifacetReader Reader { get; set; }
        public IMultifacetWriter Writer { get; set; }
    }

    [Orleans.Providers.StorageProvider(ProviderName = "MemoryStore")]
    public class MultifacetFactoryTestGrain : Grain<MultifacetFactoryTestGrainState>, IMultifacetFactoryTestGrain
    {
        public Task<IMultifacetReader> GetReader(IMultifacetTestGrain grain)
        {
            return Task.FromResult<IMultifacetReader>(grain);
        }

        public Task<IMultifacetReader> GetReader()
        {
            return Task.FromResult(State.Reader);
        }

        public Task<IMultifacetWriter> GetWriter(IMultifacetTestGrain grain)
        {
            return Task.FromResult<IMultifacetWriter>(grain);
        }

        public Task<IMultifacetWriter> GetWriter()
        {
            return Task.FromResult(State.Writer);
        }

        public Task SetReader(IMultifacetReader reader)
        {
            State.Reader = reader;
            return TaskDone.Done;
        }

        public Task SetWriter(IMultifacetWriter writer)
        {
            State.Writer = writer;
            return TaskDone.Done;
        }
    }
}