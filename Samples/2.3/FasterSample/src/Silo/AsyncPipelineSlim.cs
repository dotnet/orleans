using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Silo
{
    public class AsyncPipelineSlim
    {
        private readonly HashSet<Task> tasks = new HashSet<Task>();

        public AsyncPipelineSlim(int capacity)
        {
            Capacity = capacity;
        }

        public int Capacity { get; }

        public async Task AddAsync(Func<Task> action)
        {
            while (tasks.Count >= Capacity)
            {
                tasks.Remove(await Task.WhenAny(tasks));
            }

            tasks.Add(action());
        }

        public async Task AddRangeAsync(IEnumerable<Func<Task>> actions)
        {
            foreach (var action in actions)
            {
                await AddAsync(action);
            }
        }

        public Task WhenAll()
        {
            return Task.WhenAll(tasks);
        }
    }
}