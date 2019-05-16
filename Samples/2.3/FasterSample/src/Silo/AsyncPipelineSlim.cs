using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Silo
{
    public class AsyncPipelineSlim
    {
        private readonly HashSet<Task> running = new HashSet<Task>();
        private readonly LinkedList<TaskCompletionSource<Task>> completions = new LinkedList<TaskCompletionSource<Task>>();
        private LinkedListNode<TaskCompletionSource<Task>> nextCompletionSlot;

        public AsyncPipelineSlim(int capacity)
        {
            Capacity = capacity;
        }

        public int Capacity { get; }

        public void Add(Func<Task> action)
        {
            // make room
            while (running.Count >= Capacity)
            {
                TaskCompletionSource<Task> completion;
                lock (completions)
                {
                    completion = completions.First.Value;
                    completions.RemoveFirst();
                }
                completion.Task.Wait();
                running.Remove(completion.Task);
            }

            // add completion capacity
            var newCompletion = new TaskCompletionSource<Task>();
            lock (completions)
            {
                completions.AddLast(newCompletion);
            }

            // start the task
            var task = action();
            task.ContinueWith(x =>
            {
                TaskCompletionSource<Task> completion;
                lock (completions)
                {
                    nextCompletionSlot = nextCompletionSlot == null
                        ? completions.First
                        : nextCompletionSlot.Next;
                    completion = nextCompletionSlot.Value;
                }
                completion.TrySetResult(x);
            });
            running.Add(task);
        }

        public void AddRange(IEnumerable<Func<Task>> actions)
        {
            foreach (var action in actions)
            {
                Add(action);
            }
        }

        public void WaitAll() => Task.WaitAll(running.ToArray());
    }
}