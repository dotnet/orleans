using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Orleans;
using OrleansGrainInterfaces.MapReduce;

namespace OrleansBenchmarkGrains.MapReduce
{
    public class TransformGrain<TInput, TOutput> : DataflowGrain, ITransformGrain<TInput, TOutput>
    {
        private ITransformProcessor<TInput, TOutput> _processor;
        private bool _processingStarted ;
        private bool _proccessingStopped;

        private const bool ProcessOnThreadPool = true;

        // it should be list
        private ITargetGrain<TOutput> _target;

        // BlockingCollection has shown worse perf results for this workload types
        private readonly ConcurrentQueue<TInput> _input = new ConcurrentQueue<TInput>();

        private readonly ConcurrentQueue<TOutput> _output = new ConcurrentQueue<TOutput>();

        public Task Initialize(ITransformProcessor<TInput, TOutput> processor)
        {
            if (processor == null) throw new ArgumentNullException(nameof(processor));
            _processor = processor;
            return TaskDone.Done;
        }

        public Task<TOutput> ConsumeMessage()
        {
            throw new NotImplementedException();
        }

        public Task LinkTo(ITargetGrain<TOutput> t)
        {
            _target = t;
            return TaskDone.Done;
        }

        public Task<GrainDataflowMessageStatus> OfferMessage(TInput messageValue, bool consumeToAccept)
        {
            throw new NotImplementedException();
        }

        public Task SendAsync(TInput t)
        {
            _input.Enqueue(t);
            NotifyOfPendingWork();
            return TaskDone.Done;
        }

        public Task SendAsync(TInput t, GrainCancellationToken gct)
        {
            throw new NotImplementedException();
        }

        private void NotifyOfPendingWork()
        {
            if (_processingStarted) return;

            var orleansTs = TaskScheduler.Current;
            if (ProcessOnThreadPool)
            {
                Task.Run(async () =>
                {
                    while (!_proccessingStopped)
                    {
                        TInput itemToProcess;
                        if (!_input.TryDequeue(out itemToProcess))
                        {
                            await Task.Delay(7);
                            continue;
                        }

                        var processed = _processor.Process(itemToProcess);
                        await Task.Factory.StartNew(
                            async () => await _target.SendAsync(processed), CancellationToken.None, TaskCreationOptions.None, orleansTs);
                    }
                });
            }
            else
            {
                throw new NotImplementedException();
            }

            _processingStarted = true;
        }

        public override Task OnDeactivateAsync()
        {
            _proccessingStopped = true;
            _processingStarted = false;
            return base.OnDeactivateAsync();
        }

        public Task<List<TOutput>> ReceiveAll()
        {
            throw new NotImplementedException();
        }
    }
}
