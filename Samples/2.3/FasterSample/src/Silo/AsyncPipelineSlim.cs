using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;

namespace Silo
{
    public class AsyncPipelineSlim
    {
        private readonly ActionBlock<Func<Task>> worker;

        public AsyncPipelineSlim(int capacity)
        {
            this.worker = new ActionBlock<Func<Task>>(async action =>
            {
                await action();
            },
            new ExecutionDataflowBlockOptions
            {
                MaxDegreeOfParallelism = capacity
            });
        }

        public void AddRange(IEnumerable<Func<Task>> actions)
        {
            foreach (var action in actions)
            {
                worker.Post(action);
            }
        }

        public void Wait()
        {
            worker.Complete();
            worker.Completion.Wait();
        }
    }
}