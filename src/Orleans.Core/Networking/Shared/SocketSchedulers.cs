using System.IO.Pipelines;
using Microsoft.Extensions.Options;

namespace Orleans.Networking.Shared
{
    internal class SocketSchedulers
    {
        private static readonly PipeScheduler[] ThreadPoolSchedulerArray = new PipeScheduler[] { PipeScheduler.ThreadPool };
        private readonly int _numSchedulers;
        private readonly PipeScheduler[] _schedulers;
        private int nextScheduler;

        public SocketSchedulers(IOptions<SocketConnectionOptions> options)
        {
            var o = options.Value;
            if (o.IOQueueCount > 0)
            {
                _numSchedulers = o.IOQueueCount;
                _schedulers = new IOQueue[_numSchedulers];

                for (var i = 0; i < _numSchedulers; i++)
                {
                    _schedulers[i] = new IOQueue();
                }
            }
            else
            {
                _numSchedulers = ThreadPoolSchedulerArray.Length;
                _schedulers = ThreadPoolSchedulerArray;
            }
        }

        public PipeScheduler GetScheduler() => _schedulers[++nextScheduler % _numSchedulers];
    }
}
