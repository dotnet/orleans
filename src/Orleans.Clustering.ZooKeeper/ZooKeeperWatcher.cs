using System.Threading.Tasks;
using org.apache.zookeeper;
using Microsoft.Extensions.Logging;
using static org.apache.zookeeper.Watcher.Event;
using System.Threading;

namespace Orleans.Runtime.Membership
{
    /// <summary>
    /// the state of every ZooKeeper client and its push notifications are published using watchers.
    /// in orleans the watcher is only for debugging purposes
    /// </summary>
    internal class ZooKeeperWatcher : Watcher
    {
        private readonly ILogger logger;
        private TaskCompletionSource<KeeperState> _tcs = new();

        public ZooKeeperWatcher(ILogger logger)
        {
            this.logger = logger;
        }

        public async Task<KeeperState> WaitForConnectionAsync(CancellationToken tk)
        {
            tk.Register(static (tcs)
                => (tcs as TaskCompletionSource<KeeperException>)?.TrySetCanceled(),
                _tcs);

            return await _tcs.Task;
        }

        public override Task process(WatchedEvent ev)
        {
            if (logger.IsEnabled(LogLevel.Debug))
            {
                logger.Debug(ev.ToString());
            }

            switch (ev.getState())
            {
                case KeeperState.AuthFailed:
                    logger.LogError("ZooKeeper authentication failed", ev);
                    _tcs.TrySetResult(KeeperState.AuthFailed);
                    break;
                case KeeperState.Expired:
                    logger.LogError("ZooKeeper session expired", ev);
                    _tcs.TrySetResult(KeeperState.Expired);
                    break;
                case KeeperState.Disconnected:
                    logger.LogError("ZooKeeper disconnected", ev);
                    ResetTaskCompletionSource(ref _tcs);
                    break;
                case KeeperState.SyncConnected:
                    logger.LogInformation("ZooKeeper connected", ev);
                    _tcs.TrySetResult(KeeperState.SyncConnected);
                    break;
                case KeeperState.ConnectedReadOnly:
                    logger.LogInformation("ZooKeeper connected readonly", ev);
                    _tcs.TrySetResult(KeeperState.ConnectedReadOnly);
                    break;
            }

            return Task.CompletedTask;

            static void ResetTaskCompletionSource(ref TaskCompletionSource<KeeperState> tcs)
            {
                if (tcs.Task.IsCompleted)
                {
                    tcs = new();
                }
            }
        }
    }
}
