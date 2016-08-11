using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GrainInterfaces;
using Orleans;

namespace BenchmarkGrains.MapReduce
{
    public class TransformGrain<TInput, TOutput> : DataflowGrain, ITransformGrain<TInput, TOutput>
    {
        private ITransformProcessor<TInput, TOutput> _processor;
        private bool _processingStarted = false;
        private bool proccessingStopped { get; set; }
        private const bool ProcessOnThreadPool = true;

        // it should be list
        private ITargetGrain<TOutput> _target;

        // BlockingCollection has shown worse perf results for this workload types
        private readonly ConcurrentQueue<TInput> _input = new ConcurrentQueue<TInput>();

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
            if (!_processingStarted)
            {
                var orleansTs = TaskScheduler.Current;
                if (ProcessOnThreadPool)
                {
                    Task.Run(async () =>
                    {
                        while (!proccessingStopped)
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
        }

        public override Task OnDeactivateAsync()
        {
            proccessingStopped = true;
            return base.OnDeactivateAsync();
        }
    }
}
