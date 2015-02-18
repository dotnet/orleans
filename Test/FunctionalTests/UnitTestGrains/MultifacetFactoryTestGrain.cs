using System.Threading.Tasks;
using Orleans;

namespace MultifacetGrain
{
    public interface IMultifacetFactoryTestGrainState : IGrainState
    {
        IMultifacetReader Reader { get; set; }
        IMultifacetWriter Writer { get; set; }
    }

    public class MultifacetFactoryTestGrain : Grain<IMultifacetFactoryTestGrainState>, IMultifacetFactoryTestGrain
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