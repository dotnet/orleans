using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Orleans.Runtime.GrainDirectory
{
    internal class SingleThreadedExecutor
    {
        private readonly Action<Func<Task>> scheduleTask;
        private readonly ILogger logger;
        private readonly Queue<Operation> pendingOperations = new Queue<Operation>();
        private readonly AsyncLock executorLock = new AsyncLock();
        private readonly Func<Task> processTasks;

        public SingleThreadedExecutor(Action<Func<Task>> scheduleTask, ILogger logger)
        {
            this.scheduleTask = scheduleTask;
            this.processTasks = this.ExecutePendingOperations;
            this.logger = logger;
        }

        public TimeSpan RetryDelay { get; set; } = TimeSpan.FromMilliseconds(250);

        public void QueueTask(string name, Func<Task> action)
        {
            lock (this)
            {
                this.pendingOperations.Enqueue(new Operation(name, action));
                if (this.pendingOperations.Count <= 2)
                {
                    this.scheduleTask(this.processTasks);
                }
            }
        }

        private async Task ExecutePendingOperations()
        {
            using (await executorLock.LockAsync())
            {
                while (true)
                {
                    // Get the next operation, or exit if there are none.
                    Operation op;
                    lock (this)
                    {
                        if (this.pendingOperations.Count == 0) break;

                        op = this.pendingOperations.Peek();
                    }

                    try
                    {
                        await op.Action();
                        lock (this)
                        {
                            // Remove the successful operation from the queue.
                            this.pendingOperations.Dequeue();
                        }
                    }
                    catch (Exception exception)
                    {
                        if (this.logger.IsEnabled(LogLevel.Warning))
                        {
                            this.logger.LogWarning($"{op.Name} failed: {LogFormatter.PrintException(exception)}");
                        }

                        await Task.Delay(RetryDelay);
                    }
                }
            }
        }

        private struct Operation
        {
            public Operation(string name, Func<Task> action)
            {
                Name = name;
                Action = action;
            }

            public string Name { get; }

            public Func<Task> Action { get; }
        }
    }
}