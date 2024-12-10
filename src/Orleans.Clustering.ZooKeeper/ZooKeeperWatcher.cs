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

        public ZooKeeperWatcher(ILogger logger)
        {
            this.logger = logger;
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
                    break;
                case KeeperState.Expired:
                    logger.LogError("ZooKeeper session expired", ev);
                    break;
                case KeeperState.Disconnected:
                    logger.LogError("ZooKeeper disconnected", ev);
                    break;
                case KeeperState.SyncConnected:
                    logger.LogInformation("ZooKeeper connected", ev);
                    break;
                case KeeperState.ConnectedReadOnly:
                    logger.LogInformation("ZooKeeper connected readonly", ev);
                    break;
            }

            return Task.CompletedTask;

        }
    }
}
