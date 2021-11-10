using System;
using System.Threading;
using System.Threading.Tasks;
using Orleans;
using VotingContract;

namespace Voting.Data
{
    public partial class PollService
    {
        private class PollWatcherSubscription : IAsyncDisposable
        {
            private readonly CancellationTokenSource _cancellation = new();
            private WeakReference _watcher;
            private Task _watcherTask;
            private IPollGrain _pollGrain;
            private IPollWatcher _watcherReference;
            public PollWatcherSubscription(IPollWatcher watcher, IPollGrain pollGrain, IPollWatcher watcherReference)
            {
                _pollGrain = pollGrain;
                _watcher = new WeakReference(watcher);
                _watcherReference = watcherReference;
                _watcherTask = Task.Run(WatchPoll);
            }

            private async Task WatchPoll()
            {
                using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));

                await _pollGrain.StartWatching(_watcherReference);
                while (await timer.WaitForNextTickAsync(_cancellation.Token))
                {
                    // When the client disconnects, the .NET garbage collector can clean up the watcher object.
                    // When that happens, we will stop watching.
                    // Until then, periodically heartbeat the poll grain to let it know we're still watching.
                    if (_watcher.IsAlive)
                    {
                        try
                        {
                            await _pollGrain.StartWatching(_watcherReference);
                        }
                        catch
                        {
                            // Ignore the exception. We should log it.
                        }
                    }
                    else
                    {
                        // The poll watcher object has been cleaned up, so stop refreshing its subscription.
                        break;
                    }
                }

                // Notify the poll grain that we are no longer interested
                _pollGrain.StopWatching(_watcherReference).Ignore();
            }

            public async ValueTask DisposeAsync()
            {
                _cancellation.Cancel();
                try
                {
                    await _watcherTask;
                }
                catch
                {
                    // TODO: log
                }

                _cancellation.Dispose();
            }
        }
    }
}