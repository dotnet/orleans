using System;
using System.Threading.Tasks;

namespace Orleans.Runtime
{
    internal class AsynchAgentTask : Task
    {
        public string Name { get; }

        public AsynchAgentTask(Action action, string name) : base(action)
        {
            Name = name;
        }
    }
}