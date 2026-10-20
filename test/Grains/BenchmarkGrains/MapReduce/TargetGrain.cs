#pragma warning disable CS0618 // Preserve the legacy token benchmark implementation.

using BenchmarkGrainInterfaces.MapReduce;

namespace BenchmarkGrains.MapReduce
{
    public class TargetGrain<TInput> : DataflowGrain, ITargetGrain<TInput>
    {
        private ITargetProcessor<TInput> _processor = null!;

        public Task Init(ITargetProcessor<TInput> processor)
        {
            this._processor = processor;
            return Task.CompletedTask;
        }

        public Task<GrainDataflowMessageStatus> OfferMessage(TInput messageValue, bool consumeToAccept)
        {
            throw new NotImplementedException();
        }

        public Task SendAsync(TInput t)
        {
            throw new NotImplementedException();
        }

        public Task SendAsync(TInput t, GrainCancellationToken gct)
        {
            throw new NotImplementedException();
        }
    }

}
