using BenchmarkGrainInterfaces.MapReduce;

namespace BenchmarkGrains.MapReduce
{
    public class BufferGrain<T> : DataflowGrain, IBufferGrain<T>
    {
        private readonly List<T> _items = new List<T>();
        public Task<GrainDataflowMessageStatus> OfferMessage(T messageValue, bool consumeToAccept)
        {
            throw new NotImplementedException();
        }

        public Task SendAsync(T t)
        {
            this._items.Add(t);
            return Task.CompletedTask;
        }

#pragma warning disable CS0618 // Preserve the legacy token benchmark implementation.
        public Task SendAsync(T t, GrainCancellationToken gct)
#pragma warning restore CS0618
        {
            throw new NotImplementedException();
        }

        public Task LinkTo(ITargetGrain<T> t)
        {
            throw new NotImplementedException();
        }

        public Task<T> ConsumeMessage()
        {
            throw new NotImplementedException();
        }

        public Task<List<T>> ReceiveAll()
        {
            var items = this._items.ToList();
            this._items.Clear();
            return Task.FromResult(items);
        }
    }
}
