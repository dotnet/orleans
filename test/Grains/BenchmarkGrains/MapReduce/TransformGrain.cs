using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BenchmarkGrainInterfaces.MapReduce;
using Orleans;

namespace BenchmarkGrains.MapReduce
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
            this._processor = processor;
            return Task.CompletedTask;
        }

        public Task<TOutput> ConsumeMessage()
        {
            throw new NotImplementedException();
        }

        public Task LinkTo(ITargetGrain<TOutput> t)
        {
            this._target = t;
            return Task.CompletedTask;
        }

        public Task<GrainDataflowMessageStatus> OfferMessage(TInput messageValue, bool consumeToAccept)
        {
            throw new NotImplementedException();
        }

        public Task SendAsync(TInput t)
        {
            this._input.Enqueue(t);
            NotifyOfPendingWork();
            return Task.CompletedTask;
        }

        public Task SendAsync(TInput t, GrainCancellationToken gct)
        {
            throw new NotImplementedException();
        }

        private void NotifyOfPendingWork()
        {
            if (this._processingStarted) return;

            var orleansTs = TaskScheduler.Current;
            if (ProcessOnThreadPool)
            {
                Task.Run(async () =>
                {
                    while (!this._proccessingStopped)
                    {
                        TInput itemToProcess;
                        if (!this._input.TryDequeue(out itemToProcess))
                        {
                            await Task.Delay(7);
                            continue;
                        }

                        var processed = this._processor.Process(itemToProcess);
                        await Task.Factory.StartNew(
                            async () => await this._target.SendAsync(processed), CancellationToken.None, TaskCreationOptions.None, orleansTs);
                    }
                });
            }
            else
            {
                throw new NotImplementedException();
            }

            this._processingStarted = true;
        }

        public override Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
        {
            this._proccessingStopped = true;
            this._processingStarted = false;
            return base.OnDeactivateAsync(reason, cancellationToken);
        }

        public Task<List<TOutput>> ReceiveAll()
        {
            throw new NotImplementedException();
        }
    }
}
