using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Orleans;
using OrleansGrainInterfaces.MapReduce;

namespace OrleansBenchmarkGrains.MapReduce
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
            _items.Add(t);
            return TaskDone.Done;
        }

        public Task SendAsync(T t, GrainCancellationToken gct)
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
            var items = _items.ToList();
            _items.Clear();
            return Task.FromResult(items);
        }
    }
}