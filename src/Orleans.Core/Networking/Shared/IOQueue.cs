using System;
using System.Collections.Concurrent;
using System.IO.Pipelines;

namespace Orleans.Networking.Shared
{
    internal sealed class IOQueue : PipeScheduler
    {
        private readonly object _workSync = new object();
        private readonly ConcurrentQueue<Work> _workItems = new ConcurrentQueue<Work>();
        private static readonly Action<IOQueue> Callback = ctx => ctx.Execute();

        private bool _doingWork;

        public override void Schedule(Action<object> action, object state)
        {
            var work = new Work(action, state);

            _workItems.Enqueue(work);

            lock (_workSync)
            {
                if (!_doingWork)
                {
                    System.Threading.ThreadPool.QueueUserWorkItem(Callback, this, preferLocal: false);
                    _doingWork = true;
                }
            }
        }

        private void Execute()
        {
            while (true)
            {
                while (_workItems.TryDequeue(out var item))
                {
                    item.Callback(item.State);
                }

                lock (_workSync)
                {
                    if (_workItems.IsEmpty)
                    {
                        _doingWork = false;
                        return;
                    }
                }
            }
        }

        private readonly struct Work
        {
            public readonly Action<object> Callback;
            public readonly object State;

            public Work(Action<object> callback, object state)
            {
                Callback = callback;
                State = state;
            }
        }
    }
}
