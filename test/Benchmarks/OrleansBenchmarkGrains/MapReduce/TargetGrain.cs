﻿using System;
using System.Threading.Tasks;
using Orleans;
using OrleansGrainInterfaces.MapReduce;

namespace OrleansBenchmarkGrains.MapReduce
{
    public class TargetGrain<TInput> : DataflowGrain, ITargetGrain<TInput>
    {
        private ITargetProcessor<TInput> _processor;
        private bool _initialized;

        public Task Init(ITargetProcessor<TInput> processor)
        {
            _processor = processor;
            _initialized = true;
            return TaskDone.Done;
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
